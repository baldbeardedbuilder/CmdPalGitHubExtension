// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Api;

[TestClass]
public sealed class OperationDiagnosticsTests
{
    [TestMethod]
    public async Task NestedOperationsShareCorrelationWithoutDuplicateErrors()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var failure = new IOException("secret-exception");

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, () =>
                OperationDiagnostics.RunAsync<int>(DiagnosticEvent.SchemaRead, () => throw failure),
                area: DiagnosticArea.Notifications));

        Assert.HasCount(1, entries.Select(e => e.OperationId).Distinct());
        Assert.HasCount(1, entries.Where(e => e.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual(DiagnosticOutcome.Failed, entries[^1].Outcome);
        Assert.IsTrue(entries.All(e => e.DurationMs >= 0));
        Assert.IsTrue(entries.All(e => e.Area == DiagnosticArea.Notifications));
        Assert.DoesNotContain("secret", string.Join('\n', entries));
    }

    [TestMethod]
    public async Task SeparateOperationsHaveSeparateIds()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        await OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, () => Task.CompletedTask);
        await OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, () => Task.CompletedTask);
        Assert.HasCount(2, entries.Select(e => e.OperationId).Distinct());
    }

    [TestMethod]
    public async Task FailureDeduplicationDoesNotSuppressIndependentRetries()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var failure = new IOException("secret-exception");
        for (var retry = 0; retry < 2; retry++)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, () => Task.FromException(failure)));
        }

        Assert.HasCount(2, entries.Where(e => e.Severity == DiagnosticSeverity.Error));
        Assert.HasCount(2, entries.Select(e => e.OperationId).Distinct());
    }

    [TestMethod]
    public async Task NormalCancellationIsInformationNotError()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OperationDiagnostics.RunAsync(DiagnosticEvent.PageSearch,
                () => Task.FromCanceled(cancellation.Token), cancellationToken: cancellation.Token));
        Assert.AreEqual(DiagnosticOutcome.Cancelled, entries[^1].Outcome);
        Assert.IsTrue(entries.All(e => e.Severity == DiagnosticSeverity.Information));
    }

    [TestMethod]
    public async Task SinkFailureDoesNotChangeOperationResult()
    {
        using var sink = OperationDiagnostics.UseSink(_ => throw new InvalidOperationException("secret"));
        Assert.AreEqual(42, await OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, () => Task.FromResult(42)));
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 2)]
    public async Task SuccessfulReadsAreOptIn(bool verbose, int expectedEntries)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verbose);
        await OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, () => Task.CompletedTask, verbose: true);
        Assert.HasCount(expectedEntries, entries);
        if (verbose)
        {
            Assert.AreEqual(DiagnosticOutcome.Requested, entries[0].Outcome);
            Assert.AreEqual(DiagnosticOutcome.Completed, entries[1].Outcome);
        }
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 2)]
    public async Task ReadOnlyGraphQLSuccessUsesReadDiagnostics(bool verbose, int expectedEntries)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verbose);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var content = new StringContent("""{"query":"query { viewer { id } }"}""");
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");

        using var response = await GitHubRest.SendAsync(http, account, HttpMethod.Post,
            new Uri("https://api.github.com/graphql"), TestContext.CancellationToken, content: content);

        Assert.HasCount(expectedEntries, entries);
        if (verbose)
        {
            Assert.AreEqual(DiagnosticOutcome.Requested, entries[0].Outcome);
            Assert.AreEqual(DiagnosticOutcome.Completed, entries[^1].Outcome);
        }
    }

    [TestMethod]
    public async Task GraphQLForeignHostIsRejectedBeforeReadingContent()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var http = new HttpClient(new StubHandler(_ => throw new AssertFailedException("No request should be sent.")));
        using var content = new StringContent("""{"query":"query { viewer { id } }"}""");
        content.Dispose();
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, account, HttpMethod.Post, new Uri("https://foreign.example/graphql"),
                TestContext.CancellationToken, content: content));

        Assert.AreEqual(DiagnosticFailure.Authentication, entries.Single().Failure);
        Assert.IsFalse(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task ConcurrentOperationsKeepCorrelationAndSinksIsolated()
    {
        var first = new List<DiagnosticEntry>();
        var second = new List<DiagnosticEntry>();
        async Task Run(List<DiagnosticEntry> entries)
        {
            using var sink = OperationDiagnostics.UseSink(entries.Add);
            await OperationDiagnostics.RunAsync(DiagnosticEvent.PageLoad, async () =>
            {
                await Task.Yield();
                await OperationDiagnostics.RunAsync(DiagnosticEvent.SchemaRead, () => Task.CompletedTask);
            });
        }

        await Task.WhenAll(Task.Run(() => Run(first)), Task.Run(() => Run(second)));
        Assert.HasCount(4, first);
        Assert.HasCount(4, second);
        Assert.HasCount(1, first.Select(e => e.OperationId).Distinct());
        Assert.HasCount(1, second.Select(e => e.OperationId).Distinct());
        Assert.AreNotEqual(first[0].OperationId, second[0].OperationId);
    }

    [TestMethod]
    [DataRow("https://private.example/api/v3/repos/private-owner/private-repo/issues/secret-id?token=secret", "/repos/{owner}/{repo}/issues/{number}")]
    [DataRow("https://api.github.com/user/codespaces/private-name/stop", "/user/codespaces/{name}/stop")]
    [DataRow("https://api.github.com/agents/tasks/private-id", "/agents/tasks/{id}")]
    [DataRow("https://api.github.com/private-route/private-id", "unknown")]
    [DataRow("https://api.github.com/repos/issues/pulls/private-suffix", "unknown")]
    public void RouteTemplatesOnlyExposeKnownLiterals(string input, string expected) =>
        Assert.AreEqual(expected, OperationDiagnostics.RouteTemplate(new Uri(input)));

    [TestMethod]
    public async Task StandaloneRequestAndJsonFailureRemainCorrelated()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("secret-body"),
        }));
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");
        using var response = await GitHubRest.SendAsync(http, account, HttpMethod.Post,
            new Uri("https://api.github.com/user/codespaces"), TestContext.CancellationToken);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => GitHubRest.ReadJsonAsync(response, TestContext.CancellationToken));

        Assert.HasCount(1, entries.Select(e => e.OperationId).Distinct());
        Assert.IsTrue(entries.Any(e => e.Outcome == DiagnosticOutcome.Accepted));
        Assert.IsTrue(entries.Any(e => e.Failure == DiagnosticFailure.Schema));
        Assert.DoesNotContain("secret", string.Join('\n', entries));
    }

    [TestMethod]
    public async Task HttpFailureDoesNotTrustHeadersMethodOrPrivateRoute()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("secret-body"),
                ReasonPhrase = "secret-reason",
            };
            response.Headers.TryAddWithoutValidation("X-GitHub-Request-Id", "secret-header");
            response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "secret-rate");
            return response;
        }));
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => GitHubRest.SendAsync(http, account,
            new HttpMethod("secret-method"), new Uri("https://api.github.com/secret-route?secret-query#secret-fragment"),
            TestContext.CancellationToken));

        Assert.AreEqual("OTHER", entries[^1].Method);
        Assert.AreEqual("unknown", entries[^1].Route);
        Assert.DoesNotContain("secret", string.Join('\n', entries));
    }

    [TestMethod]
    public async Task UnexpectedRequestFailureHasAnErrorRatherThanAnUnfinishedWarning()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("secret-exception")));
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => GitHubRest.SendAsync(http, account,
            HttpMethod.Get, new Uri("https://api.github.com/user"), TestContext.CancellationToken));
        var entry = Assert.ContainsSingle(entries);
        Assert.AreEqual(DiagnosticSeverity.Error, entry.Severity);
        Assert.AreEqual(DiagnosticFailure.Unexpected, entry.Failure);
        Assert.DoesNotContain("secret", entry.ToString());
    }

    [TestMethod]
    public async Task MutationTransportFailureIsUnknownRatherThanFailedOrCompleted()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("secret-exception")));
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => GitHubRest.SendAsync(http, account, HttpMethod.Patch,
            new Uri("https://api.github.com/notifications/threads/secret-id"), TestContext.CancellationToken));

        Assert.AreEqual(DiagnosticOutcome.Unknown, entries[^1].Outcome);
        Assert.AreEqual(DiagnosticSeverity.Warning, entries[^1].Severity);
        Assert.AreEqual("/notifications/threads/{id}", entries[^1].Route);
        Assert.DoesNotContain("secret", string.Join('\n', entries));
    }

    [TestMethod]
    public async Task MutationSummaryPreservesUnknownWithoutDuplicateWarning()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException("secret")));
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "secret-login", "secret-token");
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            OperationDiagnostics.RunAsync(DiagnosticEvent.NotificationDone, () =>
                GitHubRest.SendAsync(http, account, HttpMethod.Delete,
                    new Uri("https://api.github.com/notifications/threads/secret-id"), TestContext.CancellationToken)));

        Assert.AreEqual(DiagnosticOutcome.Unknown, entries[^1].Outcome);
        Assert.AreEqual(DiagnosticFailure.Timeout, entries[^1].Failure);
        Assert.AreEqual(DiagnosticSeverity.Information, entries[^1].Severity);
        Assert.HasCount(1, entries.Where(e => e.Severity == DiagnosticSeverity.Warning));
    }

    [TestMethod]
    public async Task MutationSummaryDoesNotUpgradeAcceptanceToCompletion()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        await OperationDiagnostics.RunAsync(DiagnosticEvent.NotificationRead, () =>
        {
            using var request = OperationDiagnostics.Begin(DiagnosticEvent.RestRequest);
            request.Complete(DiagnosticOutcome.Accepted, 202, HttpMethod.Patch);
            return Task.CompletedTask;
        });

        Assert.AreEqual(DiagnosticOutcome.Accepted, entries[^1].Outcome);
    }

    [TestMethod]
    public async Task ConfirmedMutationCanCompleteAfterRequestAcceptance()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        await OperationDiagnostics.RunAsync(DiagnosticEvent.CodespaceStart, () =>
        {
            using var command = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceStart);
            using (var request = OperationDiagnostics.Begin(DiagnosticEvent.RestRequest))
            {
                request.Complete(DiagnosticOutcome.Accepted, 202, HttpMethod.Post);
            }

            command.Complete(DiagnosticOutcome.Completed);
            return Task.CompletedTask;
        });

        Assert.AreEqual(DiagnosticOutcome.Completed, entries[^1].Outcome);
    }

    [TestMethod]
    public void PostAcceptanceSchemaFailureRemainsUnknownInSummary()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        var error = new GitHubApiException("secret-message");
        using var page = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceCreate);
        using (var parser = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, verbose: true))
        {
            parser.Fail(error, DiagnosticFailure.Schema);
        }

        using (var client = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceCreate))
        {
            client.Fail(error, outcome: DiagnosticOutcome.Unknown);
        }

        page.Fail(error);
        Assert.AreEqual(DiagnosticOutcome.Unknown, entries[^1].Outcome);
        Assert.HasCount(1, entries.Where(e => e.Severity == DiagnosticSeverity.Error));
    }

    [TestMethod]
    public void PartialReadIsVisibleWithoutVerboseModeAndInheritedBySummary()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var page = OperationDiagnostics.Begin(DiagnosticEvent.PageLoad, verbose: true);
        using (var client = OperationDiagnostics.Begin(DiagnosticEvent.PageLoad, verbose: true))
        {
            client.Complete(DiagnosticOutcome.Partial);
        }

        page.Complete();
        Assert.HasCount(2, entries);
        Assert.AreEqual(DiagnosticSeverity.Warning, entries[0].Severity);
        Assert.AreEqual(DiagnosticOutcome.Partial, entries[^1].Outcome);
        Assert.AreEqual(DiagnosticSeverity.Information, entries[^1].Severity);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    public TestContext TestContext { get; set; }
}
