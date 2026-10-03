// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Api;

[TestClass]
public sealed class GitHubRestTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "secret-token");
    private static readonly Uri Endpoint = new("https://api.github.com/repos/o/r/pulls/7?access_token=secret-query");

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "GitHub didn't accept your token. Sign out and back in to fix it.")]
    [DataRow(HttpStatusCode.Forbidden, "GitHub said no. Your token might be missing a scope, or you hit a rate limit.")]
    [DataRow(HttpStatusCode.TooManyRequests, "GitHub said no. Your token might be missing a scope, or you hit a rate limit.")]
    [DataRow(HttpStatusCode.NotFound, "github.com returned 404 Not Found.")]
    public async Task SendAsync_HttpFailureLogsSafeDiagnosticsAndThrows(HttpStatusCode status, string expectedError)
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => ErrorResponse(status)));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: logs.Add));

        Assert.AreEqual(expectedError, error.Message);
        AssertSafeFailure(logs, $"failure=Http; status={(int)status}; method=GET; route=/repos/{{owner}}/{{repo}}/pulls/{{number}}");
    }

    [TestMethod]
    public async Task SendAsync_NonThrowingFailureStillLogs()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        using var response = await GitHubRest.SendAsync(
            http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, throwOnError: false, logError: logs.Add);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        AssertSafeFailure(logs, "failure=Http; status=404; method=GET; route=/repos/{owner}/{repo}/pulls/{number}");
    }

    [TestMethod]
    public async Task SendAsync_SuccessDoesNotLog()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await GitHubRest.SendAsync(
            http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: logs.Add);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public async Task SendAsync_TransportFailureLogsCategoryWithoutExceptionSecrets()
    {
        var logs = new List<string>();
        var failure = new HttpRequestException(HttpRequestError.NameResolutionError, "secret-exception");
        using var http = new HttpClient(new StubHandler(_ => throw failure));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: logs.Add));

        Assert.AreSame(failure, error.InnerException);
        AssertSafeFailure(logs, "failure=Transport");
    }

    [TestMethod]
    public async Task ReadJsonAsync_InvalidJsonLogsMetadataWithoutBody()
    {
        var logs = new List<string>();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        using var response = ErrorResponse(HttpStatusCode.OK);
        response.RequestMessage = request;

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.ReadJsonAsync(response, TestContext.CancellationToken, logs.Add));

        Assert.AreEqual("GitHub sent back something we couldn't read.", error.Message);
        AssertSafeFailure(logs, "failure=Schema; status=200; method=GET; route=/repos/{owner}/{repo}/pulls/{number}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SendAsync_TimeoutLogsAndThrowsRetryableError(bool throwOnError)
    {
        var logs = new List<string>();
        var failure = new TaskCanceledException("secret-exception");
        using var http = new HttpClient(new StubHandler(_ => throw failure));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, throwOnError: throwOnError, logError: logs.Add));

        Assert.AreSame(failure, error.InnerException);
        Assert.AreEqual("The request to api.github.com timed out. Try again.", error.Message);
        AssertSafeFailure(logs, "failure=Timeout");
    }

    [TestMethod]
    public async Task SendAsync_CallerCancellationDoesNotLogAnError()
    {
        var logs = new List<string>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, cancellation.Token, logError: logs.Add));

        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public void LogEndpoint_RemovesCredentialsQueryAndFragment()
    {
        var uri = new Uri("https://user:secret-password@api.github.com/repos/o/r/pulls/7?token=secret-query#secret-fragment");

        Assert.AreEqual("/repos/{owner}/{repo}/pulls/{number}", GitHubRest.LogEndpoint(uri));
        Assert.AreEqual("unknown", GitHubRest.LogEndpoint(null));
    }

    [TestMethod]
    public async Task GetSubjectAsync_HttpFailurePreservesActionableError()
    {
        using var http = new HttpClient(new StubHandler(_ => ErrorResponse(HttpStatusCode.Unauthorized)));
        var client = new NotificationsClient(http);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetSubjectAsync(Account, Endpoint, TestContext.CancellationToken));

        Assert.AreEqual("GitHub didn't accept your token. Sign out and back in to fix it.", error.Message);
    }

    [TestMethod]
    public async Task GetSubjectAsync_TimeoutThrowsActionableError()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException("secret-exception")));
        var client = new NotificationsClient(http);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetSubjectAsync(Account, Endpoint, TestContext.CancellationToken));

        Assert.AreEqual("The request to api.github.com timed out. Try again.", error.Message);
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("secret-body") };
        response.Headers.Add("X-GitHub-Request-Id", "test-request");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "1790975000");
        response.Headers.Add("X-GitHub-SSO", "partial-results; organizations=21955855");
        return response;
    }

    private static void AssertSafeFailure(List<string> logs, string expected)
    {
        var log = Assert.ContainsSingle(logs);
        StringAssert.Contains(log, expected);
        StringAssert.Contains(log, "operation-id=");
        StringAssert.Contains(log, "duration-ms=");
        StringAssert.Contains(log, "severity=Error; outcome=Failed");
        Assert.DoesNotContain("secret", log);
        Assert.DoesNotContain("test-request", log);
        Assert.DoesNotContain("api.github.com", log);
        Assert.DoesNotContain("/repos/o/r", log);
    }

    [TestMethod]
    public async Task SendAsync_SsoRequiredThrowsAuthorizeUrl()
    {
        var authorize = "https://github.com/orgs/microsoft/sso?authorization_request=secret";
        using var http = new HttpClient(new StubHandler(_ => SsoResponse($"required; url={authorize}")));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: _ => { }));

        Assert.AreEqual("The microsoft organization requires SAML single sign-on. Authorize this app for microsoft, then refresh.", error.Message);
        Assert.AreEqual(new Uri(authorize), error.AuthorizeUrl);
    }

    [TestMethod]
    [DataRow("required")]
    [DataRow("required; url=https://evil.example.com/orgs/microsoft/sso")]
    [DataRow("required; url=http://github.com/orgs/microsoft/sso")]
    public async Task SendAsync_SsoRequiredWithoutTrustedUrlHasNoLink(string header)
    {
        using var http = new HttpClient(new StubHandler(_ => SsoResponse(header)));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: _ => { }));

        Assert.AreEqual("An organization requires SAML single sign-on. Authorize this app for it on GitHub, then refresh.", error.Message);
        Assert.IsNull(error.AuthorizeUrl);
    }

    private static HttpResponseMessage SsoResponse(string header)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-GitHub-SSO", header);
        return response;
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NoContent)]
    [DataRow(HttpStatusCode.OK)]
    [DataRow(HttpStatusCode.Accepted)]
    public async Task SendMutationAsync_EmptySuccessDoesNotRequireJson(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        using var result = await GitHubRest.SendMutationAsync(
            http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken, logError: _ => { });

        Assert.AreEqual(status, result.StatusCode);
        Assert.AreEqual(status == HttpStatusCode.Accepted, result.IsAccepted);
        Assert.IsNull(result.Json);
    }

    [TestMethod]
    public async Task SendMutationAsync_AcceptedJsonIsNotCompletion()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("""{"state":"queued"}"""),
        }));
        using var result = await GitHubRest.SendMutationAsync(
            http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken, logError: _ => { });

        Assert.IsTrue(result.IsAccepted);
        Assert.AreEqual("queued", result.Json!.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SendMutationAsync_TransportFailureHasUnknownOutcome(bool timeout)
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ =>
            throw (timeout ? new TaskCanceledException("secret") : new HttpRequestException("secret"))));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken, logError: logs.Add));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("secret", Assert.ContainsSingle(logs));
        Assert.Contains("before retrying", error.Message);
    }

    [TestMethod]
    public async Task SendMutationAsync_InvalidSuccessfulBodyHasUnknownOutcome()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("secret-body"),
        }));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken, logError: logs.Add));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.DoesNotContain("secret-body", Assert.ContainsSingle(logs));
    }

    [TestMethod]
    public async Task SendMutationAsync_SchemaWrapperPreservesCorrelationWithoutDuplicateWarnings()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: true);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("secret-body"),
        }));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            OperationDiagnostics.RunAsync(DiagnosticEvent.Mutation, () =>
                GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken)));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.IsTrue(OperationDiagnostics.HasFailure(error));
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        var warning = entries.Single(entry => entry.Severity == DiagnosticSeverity.Warning);
        Assert.AreEqual(DiagnosticEvent.SchemaRead, warning.Event);
        Assert.AreEqual(DiagnosticFailure.Schema, warning.Failure);
        Assert.AreEqual(201, warning.Status);
        Assert.AreEqual(DiagnosticOutcome.Unknown, entries[^1].Outcome);
        Assert.AreEqual(DiagnosticSeverity.Information, entries[^1].Severity);
        Assert.DoesNotContain("secret-body", string.Join('\n', entries));
    }

    [TestMethod]
    public async Task SendMutationAsync_SsoDiagnosticsArePreserved()
    {
        using var http = new HttpClient(new StubHandler(_ =>
            SsoResponse("required; url=https://github.com/orgs/example/sso")));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken, logError: _ => { }));

        Assert.IsFalse(error.OutcomeUnknown);
        Assert.AreEqual(new Uri("https://github.com/orgs/example/sso"), error.AuthorizeUrl);
    }

    [TestMethod]
    public async Task SendMutationAsync_CallerCancellationDoesNotLog()
    {
        var logs = new List<string>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, Endpoint, cancellation.Token, logError: logs.Add));

        Assert.IsEmpty(logs);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError, true)]
    [DataRow(HttpStatusCode.Forbidden, false)]
    public async Task SendMutationAsync_OnlyUncertainHttpFailuresRequireReconciliation(HttpStatusCode status, bool unknown)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, Endpoint, TestContext.CancellationToken, logError: _ => { }));

        Assert.AreEqual(unknown, error.OutcomeUnknown);
    }

    [TestMethod]
    [DataRow("/graphql", "query { viewer { id } }")]
    [DataRow("/graphql", "{ viewer { id } }")]
    [DataRow("/api/graphql", "query NodeId($number: Int!) { node(id: $number) { id } }")]
    public async Task SendAsync_GraphQLQueryTimeoutIsSafeToRetry(string path, string query)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException()));
        using var content = JsonContent.Create(new { query });

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Post, new Uri("https://api.github.com" + path),
                TestContext.CancellationToken, content: content));

        Assert.AreEqual("The request to api.github.com timed out. Try again.", error.Message);
        Assert.IsFalse(error.OutcomeUnknown);
        var failure = entries.Single(entry => entry.Failure == DiagnosticFailure.Timeout);
        Assert.AreEqual(DiagnosticOutcome.Failed, failure.Outcome);
        Assert.AreEqual(DiagnosticSeverity.Error, failure.Severity);
    }

    [TestMethod]
    [DataRow("mutation { updateIssue(input: {}) { clientMutationId } }")]
    [DataRow("query Read { viewer { id } } mutation Write { updateIssue(input: {}) { clientMutationId } }")]
    [DataRow("query { viewer { id } } # mutation stays conservative")]
    public async Task SendAsync_GraphQLMutationDocumentTimeoutRemainsUnknown(string query)
    {
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException()));
        using var content = JsonContent.Create(new { query, operationName = "Read" });

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Post, new Uri("https://api.github.com/graphql"),
                TestContext.CancellationToken, content: content, logError: _ => { }));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.Contains("before retrying", error.Message);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.InternalServerError)]
    [DataRow(HttpStatusCode.BadGateway)]
    public async Task SendAsync_GraphQLQueryServerFailureDoesNotImplyUncertainWrite(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)));
        using var content = JsonContent.Create(new { query = "query { viewer { id } }" });

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Post, new Uri("https://api.github.com/graphql"),
                TestContext.CancellationToken, content: content, logError: _ => { }));

        Assert.IsFalse(error.OutcomeUnknown);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("not JSON")]
    [DataRow("""{"query":42}""")]
    [DataRow("""[{"query":"query { viewer { id } }"}]""")]
    public async Task SendAsync_UnrecognizedGraphQLBodyStaysConservative(string body)
    {
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException()));
        using var content = new StringContent(body);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Post, new Uri("https://api.github.com/graphql"),
                TestContext.CancellationToken, content: content, logError: _ => { }));

        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task SendMutationAsync_ExplicitMutationDoesNotInferReadOnlyFromBody()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException()));
        using var content = JsonContent.Create(new { query = "query { viewer { id } }" });

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendMutationAsync(http, Account, HttpMethod.Post, new Uri("https://api.github.com/graphql"),
                TestContext.CancellationToken, content: content, logError: _ => { }));

        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task SendAsync_RestPostWithQueryFieldRemainsMutation()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException()));
        using var content = JsonContent.Create(new { query = "query { viewer { id } }" });

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Post, Endpoint,
                TestContext.CancellationToken, content: content, logError: _ => { }));

        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NotificationMutation_AcceptsEmptyAcceptedResponse(bool done)
    {
        HttpMethod? method = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            method = request.Method;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }));
        var client = new NotificationsClient(http);

        if (done)
        {
            await client.MarkAsDoneAsync(Account, "thread", TestContext.CancellationToken);
        }
        else
        {
            await client.MarkAsReadAsync(Account, "thread", TestContext.CancellationToken);
        }

        Assert.AreEqual(done ? HttpMethod.Delete : HttpMethod.Patch, method);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    public TestContext TestContext { get; set; }
}
