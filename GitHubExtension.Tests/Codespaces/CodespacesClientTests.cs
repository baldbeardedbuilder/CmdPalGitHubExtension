// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using Moq.Protected;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public class CodespacesClientTests
{
    private const string CodespaceJson = """
        {
          "name": "octocat-hello-abc",
          "display_name": "My workspace",
          "repository": { "full_name": "octocat/hello" },
          "git_status": { "ref": "feature/codespaces" },
          "state": "Available",
          "last_used_at": "2025-06-01T11:48:00Z",
          "web_url": "https://octocat-hello-abc.github.dev"
        }
        """;

    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ParseCodespaces_ReadsEveryField()
    {
        using var json = JsonDocument.Parse($$"""{"total_count": 1, "codespaces": [{{CodespaceJson}}]}""");

        var codespace = CodespacesClient.ParseCodespaces(json.RootElement).Single();

        Assert.AreEqual("octocat-hello-abc", codespace.Name);
        Assert.AreEqual("My workspace", codespace.DisplayName);
        Assert.AreEqual("octocat/hello", codespace.RepositoryFullName);
        Assert.AreEqual("feature/codespaces", codespace.Branch);
        Assert.AreEqual("Available", codespace.State);
        Assert.AreEqual(new DateTimeOffset(2025, 6, 1, 11, 48, 0, TimeSpan.Zero), codespace.LastUsedAt);
        Assert.AreEqual(new Uri("https://octocat-hello-abc.github.dev"), codespace.WebUrl);
    }

    [TestMethod]
    public void ParseCodespace_HandlesMissingOptionalFields()
    {
        using var json = JsonDocument.Parse("""
            {"name":"a", "web_url":"https://a.github.dev", "repository":{"full_name":"o/r"}, "git_status":null}
            """);

        var codespace = CodespacesClient.ParseCodespace(json.RootElement)!;

        Assert.IsNull(codespace.DisplayName);
        Assert.IsNull(codespace.Branch);
        Assert.AreEqual("Unknown", codespace.State);
        Assert.AreEqual(DateTimeOffset.MinValue, codespace.LastUsedAt);
    }

    [TestMethod]
    [DataRow("42")]
    [DataRow("""{"name":"a", "web_url":"https://a.github.dev"}""")]
    [DataRow("""{"name":"a", "web_url":"https://a.github.dev", "repository":null}""")]
    [DataRow("""{"name":"a", "web_url":"https://a.github.dev", "repository":42}""")]
    [DataRow("""{"name":"", "web_url":"https://a.github.dev", "repository":{"full_name":"o/r"}}""")]
    [DataRow("""{"name":"a", "web_url":"not a URL", "repository":{"full_name":"o/r"}}""")]
    [DataRow("""{"name":"a", "web_url":"file:///C:/test", "repository":{"full_name":"o/r"}}""")]
    public void ParseCodespace_SkipsIncompleteOrUnsafeEntries(string body)
    {
        using var json = JsonDocument.Parse(body);

        Assert.IsNull(CodespacesClient.ParseCodespace(json.RootElement));
    }

    [TestMethod]
    public void ParseCodespaces_KeepsValidEntries()
    {
        using var json = JsonDocument.Parse($$"""{"codespaces":[42, {{CodespaceJson}}, {"name":"broken"}]}""");

        Assert.AreEqual("octocat-hello-abc", CodespacesClient.ParseCodespaces(json.RootElement).Single().Name);
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("""{"codespaces":null}""")]
    public void ParseCodespaces_InvalidEnvelopeShowsAnError(string body)
    {
        using var json = JsonDocument.Parse(body);

        var error = Assert.ThrowsExactly<GitHubApiException>(() => CodespacesClient.ParseCodespaces(json.RootElement));

        Assert.AreEqual("GitHub sent back a codespaces list we couldn't read.", error.Message);
    }

    [TestMethod]
    public async Task GetCodespacesAsync_UsesAuthenticatedEndpointAndPagination()
    {
        var next = new Uri("https://api.github.com/user/codespaces?per_page=50&page=2");
        using var handler = new StubHandler(HttpStatusCode.OK, $$"""{"codespaces":[{{CodespaceJson}}]}""", next);
        using var http = new HttpClient(handler);
        var client = new CodespacesClient(http);

        var result = await client.GetCodespacesAsync(Account, null, TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces?per_page=50"), handler.Url);
        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual("Bearer t", handler.Authorization);
        Assert.AreEqual("application/vnd.github+json", handler.Accept);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
        Assert.AreEqual(next, result.NextPage);
        Assert.AreEqual("octocat-hello-abc", result.Codespaces.Single().Name);

        await client.GetCodespacesAsync(Account, result.NextPage, TestContext.CancellationToken);
        Assert.AreEqual(next, handler.Url);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    public async Task StopCodespaceAsync_PostsAuthenticatedStopEndpointAndReadsState()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson
            .Replace("Available", "ShuttingDown", StringComparison.Ordinal)
            .Replace("octocat-hello-abc", "workspace/name", StringComparison.Ordinal));
        using var http = new HttpClient(handler);

        var codespace = await new CodespacesClient(http).StopCodespaceAsync(Account, "workspace/name", TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace%2Fname/stop"), handler.Url);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("Bearer " + Account.Token, handler.Authorization);
        Assert.AreEqual("application/vnd.github+json", handler.Accept);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
        Assert.AreEqual("workspace/name", codespace.Name);
        Assert.AreEqual("ShuttingDown", codespace.State);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task StartCodespaceAsync_PostsAuthenticatedStartEndpointAndReadsState()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson
            .Replace("Available", "Starting", StringComparison.Ordinal)
            .Replace("octocat-hello-abc", "workspace/name", StringComparison.Ordinal));
        using var http = new HttpClient(handler);

        var codespace = await new CodespacesClient(http).StartCodespaceAsync(Account, "workspace/name", TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace%2Fname/start"), handler.Url);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("Bearer " + Account.Token, handler.Authorization);
        Assert.AreEqual("application/vnd.github+json", handler.Accept);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
        Assert.AreEqual("workspace/name", codespace.Name);
        Assert.AreEqual("Starting", codespace.State);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetCodespaceAsync_UsesAuthenticatedEscapedEndpointAndReadsState()
    {
        using var handler = new StubHandler(HttpStatusCode.OK,
            CodespaceJson.Replace("\"name\": \"octocat-hello-abc\"", "\"name\": \"workspace/name\"", StringComparison.Ordinal));
        using var http = new HttpClient(handler);

        var codespace = await new CodespacesClient(http).GetCodespaceAsync(Account, "workspace/name", TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace%2Fname"), handler.Url);
        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual("Bearer " + Account.Token, handler.Authorization);
        Assert.AreEqual("application/vnd.github+json", handler.Accept);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
        Assert.AreEqual("workspace/name", codespace.Name);
        Assert.AreEqual("Available", codespace.State);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "GitHub didn't accept your token.")]
    [DataRow(HttpStatusCode.Forbidden, "GitHub said no.")]
    [DataRow(HttpStatusCode.NotFound, "github.com returned 404")]
    public async Task GetCodespaceAsync_SurfacesDeniedAccess(HttpStatusCode status, string message)
    {
        using var handler = new StubHandler(status, "{}");
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).GetCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.StartsWith(message, error.Message);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("not JSON", "GitHub sent back something we couldn't read.")]
    [DataRow("{}", "GitHub sent back a codespace we couldn't read.")]
    public async Task GetCodespaceAsync_InvalidResponseShowsAnError(string body, string message)
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).GetCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.AreEqual(message, error.Message);
    }

    [TestMethod]
    public async Task GetCodespaceAsync_EnterpriseServerDoesNotMakeARequest()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson);
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).GetCodespaceAsync(
                new GitHubAccount(host!, "mona", "t"), "one", TestContext.CancellationToken));

        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetCodespaceAsync_CallerCancellationIsNotAnApiError()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .Returns((HttpRequestMessage _, CancellationToken token) =>
                Task.FromCanceled<HttpResponseMessage>(token));
        using var http = new HttpClient(handler.Object);
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CodespacesClient(http).GetCodespaceAsync(Account, "one", cancellation.Token));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.PaymentRequired, "github.com returned 402")]
    [DataRow(HttpStatusCode.Conflict, "github.com returned 409")]
    [DataRow(HttpStatusCode.Unauthorized, "GitHub didn't accept your token.")]
    [DataRow(HttpStatusCode.Forbidden, "GitHub said no.")]
    public async Task StartCodespaceAsync_SurfacesApiErrors(HttpStatusCode status, string message)
    {
        using var handler = new StubHandler(status, "{}");
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.StartsWith(message, error.Message);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task StartCodespaceAsync_EnterpriseServerDoesNotMakeARequest()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(
                new GitHubAccount(host!, "mona", "t"),
                "one",
                TestContext.CancellationToken));

        Assert.Contains("isn't available on GitHub Enterprise Server", error.Message);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task StartCodespaceAsync_TimeoutShowsAnError()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());
        using var http = new HttpClient(handler.Object);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.StartsWith("GitHub took too long to start this codespace.", error.Message);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "GitHub didn't accept your token.")]
    [DataRow(HttpStatusCode.Forbidden, "GitHub said no.")]
    [DataRow(HttpStatusCode.NotFound, "github.com returned 404")]
    [DataRow(HttpStatusCode.InternalServerError, "github.com returned 500")]
    public async Task StopCodespaceAsync_SurfacesApiErrors(HttpStatusCode status, string message)
    {
        using var handler = new StubHandler(status, "{}");
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StopCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.StartsWith(message, error.Message);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("not JSON", "GitHub sent back something we couldn't read.")]
    [DataRow("{}", "GitHub sent back a codespace we couldn't read.")]
    public async Task StopCodespaceAsync_InvalidResponseShowsAnError(string body, string message)
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StopCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.AreEqual(message, error.Message);
    }

    [TestMethod]
    public async Task StopCodespaceAsync_EnterpriseServerDoesNotMakeARequest()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StopCodespaceAsync(new GitHubAccount(host!, "mona", "t"), "one", TestContext.CancellationToken));

        Assert.Contains("isn't available on GitHub Enterprise Server", error.Message);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task StopCodespaceAsync_TimeoutShowsAnError()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());
        using var http = new HttpClient(handler.Object);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StopCodespaceAsync(Account, "one", TestContext.CancellationToken));

        Assert.StartsWith("GitHub took too long to close this codespace.", error.Message);
    }

    [TestMethod]
    public async Task CreateCodespaceAsync_ResolvesRepositoryAndPostsRepositoryIdAndBranch()
    {
        using var handler = new CreateCodespaceHandler(
            (HttpStatusCode.OK, """{"id":12345}"""),
            (HttpStatusCode.Created, CodespaceJson),
            (HttpStatusCode.OK, CodespaceJson));
        using var http = new HttpClient(handler);

        var codespace = await new CodespacesClient(http).CreateCodespaceAsync(
            Account,
            "octocat/hello",
            "feature/codespaces",
            TestContext.CancellationToken);

        Assert.AreEqual("octocat-hello-abc", codespace.Name);
        Assert.HasCount(3, handler.Requests);
        Assert.AreEqual(new Uri("https://api.github.com/repos/octocat/hello"), handler.Requests[0].Url);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces"), handler.Requests[1].Url);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        Assert.AreEqual(12345, body.RootElement.GetProperty("repository_id").GetInt64());
        Assert.AreEqual("feature/codespaces", body.RootElement.GetProperty("ref").GetString());
        Assert.AreEqual(HttpMethod.Get, handler.Requests[2].Method);
        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/octocat-hello-abc"), handler.Requests[2].Url);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NoContent)]
    [DataRow(HttpStatusCode.Accepted)]
    public async Task StartCodespaceAsync_EmptyResponseReadsAuthoritativeState(HttpStatusCode status)
    {
        using var handler = new CreateCodespaceHandler(
            (status, ""),
            (HttpStatusCode.OK, CodespaceJson.Replace("Available", "Starting", StringComparison.Ordinal)));
        using var http = new HttpClient(handler);

        var result = await new CodespacesClient(http).StartCodespaceAsync(Account, "octocat-hello-abc", TestContext.CancellationToken);

        Assert.AreEqual("Starting", result.State);
        Assert.HasCount(2, handler.Requests);
        Assert.AreEqual(HttpMethod.Get, handler.Requests[1].Method);
    }

    [TestMethod]
    public async Task StartCodespaceAsync_RejectsResponseForDifferentTarget()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, CodespaceJson));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(Account, "different", TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.Contains("different Codespace", error.Message);
    }

    [TestMethod]
    public async Task StopCodespaceAsync_AcceptedBodyIsNotAuthoritative()
    {
        using var handler = new CreateCodespaceHandler(
            (HttpStatusCode.Accepted, CodespaceJson.Replace("Available", "Shutdown", StringComparison.Ordinal)),
            (HttpStatusCode.OK, CodespaceJson.Replace("Available", "ShuttingDown", StringComparison.Ordinal)));
        using var http = new HttpClient(handler);

        var result = await new CodespacesClient(http).StopCodespaceAsync(Account, "octocat-hello-abc", TestContext.CancellationToken);

        Assert.AreEqual("ShuttingDown", result.State);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task StartCodespaceAsync_FailedReadAfterAcceptedKeepsUnknownOutcomeAndSso()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: true);
        using var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Accepted),
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.Add("X-GitHub-SSO", "required; url=https://github.com/orgs/example/sso");
                return response;
            });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(Account, "octocat-hello-abc", TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.AreEqual(new Uri("https://github.com/orgs/example/sso"), error.AuthorizeUrl);
        Assert.AreEqual(2, handler.RequestCount);
        Assert.IsTrue(OperationDiagnostics.HasFailure(error));
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        Assert.HasCount(1, entries.Where(entry => entry.Severity == DiagnosticSeverity.Error));
        var terminal = entries.Last(entry => entry.Event == DiagnosticEvent.CodespaceStart);
        Assert.AreEqual(DiagnosticOutcome.Unknown, terminal.Outcome);
        Assert.AreEqual(DiagnosticSeverity.Information, terminal.Severity);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Created)]
    [DataRow(HttpStatusCode.Accepted)]
    public async Task CreateCodespaceAsync_EmptySuccessIsUnknownAndNeverResent(HttpStatusCode status)
    {
        using var handler = new CreateCodespaceHandler(
            (HttpStatusCode.OK, """{"id":12345}"""),
            (status, ""));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreateCodespaceAsync(Account, "octocat/hello", null, TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.HasCount(2, handler.Requests);
    }

    [TestMethod]
    public async Task CreateCodespaceAsync_VerifiesBranchGitHubMayIgnore()
    {
        using var handler = new CreateCodespaceHandler(
            (HttpStatusCode.OK, """{"id":12345}"""),
            (HttpStatusCode.Created, CodespaceJson),
            (HttpStatusCode.OK, CodespaceJson.Replace("feature/codespaces", "main", StringComparison.Ordinal)));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreateCodespaceAsync(Account, "octocat/hello", "feature/codespaces", TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.Contains("doesn't match", error.Message);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    public async Task CreateCodespaceAsync_TimeoutIsNotResubmitted()
    {
        using var handler = new SequenceHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":12345}""") },
            _ => throw new TaskCanceledException());
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreateCodespaceAsync(Account, "octocat/hello", null, TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.AreEqual(2, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \t ")]
    [DataRow(" feature/\"branch\\\n\t\u263a ")]
    public async Task CreateCodespaceAsync_PreservesLongIdAndOptionalEscapedBranch(string? branch)
    {
        using var handler = new CreateCodespaceHandler(
            (HttpStatusCode.OK, """{"id":9223372036854775807}"""),
            (HttpStatusCode.Created, CodespaceJson));
        using var http = new HttpClient(handler);

        await new CodespacesClient(http).CreateCodespaceAsync(Account, "octocat/hello", branch, TestContext.CancellationToken);

        Assert.HasCount(2, handler.Requests);
        using var body = JsonDocument.Parse(handler.Requests[1].Body!);
        var root = body.RootElement;
        Assert.AreEqual(JsonValueKind.Number, root.GetProperty("repository_id").ValueKind);
        Assert.AreEqual(long.MaxValue, root.GetProperty("repository_id").GetInt64());
        if (string.IsNullOrWhiteSpace(branch))
        {
            Assert.IsFalse(root.TryGetProperty("ref", out _));
            Assert.AreEqual(1, root.EnumerateObject().Count());
        }
        else
        {
            Assert.AreEqual(branch.Trim(), root.GetProperty("ref").GetString());
            Assert.AreEqual(2, root.EnumerateObject().Count());
        }
    }

    [TestMethod]
    public async Task CreateCodespaceAsync_RejectsInvalidRepositoryBeforeSendingRequest()
    {
        using var handler = new CreateCodespaceHandler((HttpStatusCode.OK, """{"id":12345}"""));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreateCodespaceAsync(Account, "not-a-repository", null, TestContext.CancellationToken));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task CreateCodespaceAsync_EnterpriseServerDoesNotMakeARequest()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new CreateCodespaceHandler((HttpStatusCode.OK, """{"id":12345}"""));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreateCodespaceAsync(
                new GitHubAccount(host!, "mona", "t"),
                "octocat/hello",
                null,
                TestContext.CancellationToken));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "GitHub didn't accept your token.")]
    [DataRow(HttpStatusCode.Forbidden, "GitHub said no.")]
    [DataRow(HttpStatusCode.TooManyRequests, "GitHub said no.")]
    [DataRow(HttpStatusCode.InternalServerError, "github.com returned 500")]
    public async Task GetCodespacesAsync_SurfacesApiErrors(HttpStatusCode status, string message)
    {
        using var http = new HttpClient(new StubHandler(status, "{}"));
        var client = new CodespacesClient(http);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetCodespacesAsync(Account, null, TestContext.CancellationToken));

        Assert.StartsWith(message, error.Message);
    }

    [TestMethod]
    public async Task GetCodespacesAsync_EmptyListIsSuccessful()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, """{"total_count":0,"codespaces":[]}"""));

        var result = await new CodespacesClient(http).GetCodespacesAsync(Account, null, TestContext.CancellationToken);

        Assert.IsEmpty(result.Codespaces);
        Assert.IsNull(result.NextPage);
    }

    [TestMethod]
    public async Task GetCodespacesAsync_InvalidJsonShowsAnError()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, "not JSON"));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).GetCodespacesAsync(Account, null, TestContext.CancellationToken));

        Assert.AreEqual("GitHub sent back something we couldn't read.", error.Message);
    }

    [TestMethod]
    public async Task GetCodespacesAsync_RejectsForeignPaginationBeforeSendingToken()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"codespaces":[]}""");
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).GetCodespacesAsync(Account, new Uri("https://example.com/user/codespaces"), TestContext.CancellationToken));

        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetCodespacesAsync_EnterpriseServerDoesNotMakeARequest()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new StubHandler(HttpStatusCode.OK, """{"codespaces":[]}""");
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).GetCodespacesAsync(new GitHubAccount(host!, "mona", "t"), null, TestContext.CancellationToken));

        Assert.Contains("isn't available on GitHub Enterprise Server", error.Message);
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [DataRow("""{"has_uncommitted_changes":true,"has_unpushed_changes":true}""", true, true)]
    [DataRow("""{"has_uncommitted_changes":false,"has_unpushed_changes":false}""", false, false)]
    [DataRow("""{"has_uncommitted_changes":true,"has_unpushed_changes":false}""", true, false)]
    [DataRow("{}", null, null)]
    [DataRow("null", null, null)]
    [DataRow("42", null, null)]
    [DataRow("""{"has_uncommitted_changes":"false","has_unpushed_changes":0}""", null, null)]
    [DataRow("""{"has_uncommitted_changes":null,"has_unpushed_changes":[]}""", null, null)]
    public void ParseCodespace_GitStatusBooleansNeverAssumeUnknownIsClean(string status, bool? uncommitted, bool? unpushed)
    {
        using var json = JsonDocument.Parse($$"""
            {"name":"one","repository":{"full_name":"o/r"},"web_url":"https://one.github.dev","git_status":{{status}}}
            """);
        var codespace = CodespacesClient.ParseCodespace(json.RootElement)!;
        Assert.AreEqual(uncommitted, codespace.HasUncommittedChanges);
        Assert.AreEqual(unpushed, codespace.HasUnpushedChanges);
    }

    [TestMethod]
    public void ParseCodespace_MissingGitStatusIsUnknown()
    {
        using var json = JsonDocument.Parse("""
            {"name":"one","repository":{"full_name":"o/r"},"web_url":"https://one.github.dev"}
            """);
        var codespace = CodespacesClient.ParseCodespace(json.RootElement)!;
        Assert.IsNull(codespace.HasUncommittedChanges);
        Assert.IsNull(codespace.HasUnpushedChanges);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Accepted)]
    [DataRow(HttpStatusCode.NoContent)]
    public async Task DeleteCodespaceAsync_UsesEscapedAuthenticatedEndpointWithoutRequiringBody(HttpStatusCode status)
    {
        using var handler = new StubHandler(status, "");
        using var http = new HttpClient(handler);
        await new CodespacesClient(http).DeleteCodespaceAsync(Account, "workspace/name", TestContext.CancellationToken);
        Assert.AreEqual(HttpMethod.Delete, handler.Method);
        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace%2Fname"), handler.Url);
        Assert.AreEqual(new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Account.Token).ToString(), handler.Authorization);
        Assert.AreEqual("application/vnd.github+json", handler.Accept);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetCodespaceAsync_ReadsFreshGitStatus()
    {
        using var handler = new StubHandler(HttpStatusCode.OK,
            CodespaceJson.Replace("\"ref\": \"feature/codespaces\"", "\"has_uncommitted_changes\": true, \"has_unpushed_changes\": false", StringComparison.Ordinal));
        using var http = new HttpClient(handler);
        var details = await new CodespacesClient(http).GetCodespaceAsync(Account, "octocat-hello-abc", TestContext.CancellationToken);
        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/octocat-hello-abc"), handler.Url);
        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.IsTrue(details.HasUncommittedChanges);
        Assert.IsFalse(details.HasUnpushedChanges);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.NotModified)]
    public async Task DeleteCodespaceAsync_AccessFailureIsNotConfirmation(HttpStatusCode status)
    {
        using var handler = new StubHandler(status, "");
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).DeleteCodespaceAsync(Account, "one", TestContext.CancellationToken));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeleteAndDetails_EnterpriseServerNeverMakesRequest(bool delete)
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        var account = new GitHubAccount(host!, "mona", "t");
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson);
        using var http = new HttpClient(handler);
        var client = new CodespacesClient(http);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(async () =>
        {
            if (delete)
            {
                await client.DeleteCodespaceAsync(account, "one", TestContext.CancellationToken);
            }
            else
            {
                await client.GetCodespaceAsync(account, "one", TestContext.CancellationToken);
            }
        });
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task DeleteCodespaceAsync_TimeoutSuggestsRefreshAndDoesNotRepeatRequest()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new TaskCanceledException());
        using var http = new HttpClient(handler.Object);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).DeleteCodespaceAsync(Account, "one", TestContext.CancellationToken));
        Assert.Contains("Refresh", error.Message);
        handler.Protected().Verify("SendAsync", Times.Once(), ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [TestMethod]
    [DataRow("""{"codespaces":[42]}""")]
    [DataRow("""{"codespaces":[],"total_count":"0"}""")]
    public async Task GetCodespacesAsync_MalformedListCannotConfirmAbsence(string body)
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, body));
        var result = await new CodespacesClient(http).GetCodespacesAsync(Account, null, TestContext.CancellationToken);
        Assert.IsFalse(result.IsComplete);
    }

    [TestMethod]
    [DataRow("""{"ahead":2,"behind":3}""", 2, 3)]
    [DataRow("""{"ahead":0,"behind":0}""", 0, 0)]
    [DataRow("""{"ahead":-1,"behind":"2"}""", null, null)]
    [DataRow("""{"ahead":null,"behind":true}""", null, null)]
    [DataRow("{}", null, null)]
    public void ParseCodespace_ReadsOnlyValidOptionalCommitCounts(string status, int? ahead, int? behind)
    {
        using var json = JsonDocument.Parse($$"""
            {"name":"one","repository":{"full_name":"o/r"},"web_url":"https://one.github.dev","git_status":{{status}}}
            """);
        var codespace = CodespacesClient.ParseCodespace(json.RootElement)!;
        Assert.AreEqual(ahead, codespace.Ahead);
        Assert.AreEqual(behind, codespace.Behind);
    }

    [TestMethod]
    public async Task GetCodespacesAsync_TargetWithMalformedDetailsCannotConfirmAbsence()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, """
            {"total_count":1,"codespaces":[{"name":"one","repository":null,"web_url":"invalid"}]}
            """));
        var result = await new CodespacesClient(http).GetCodespacesAsync(Account, null, TestContext.CancellationToken);
        Assert.IsEmpty(result.Codespaces);
        Assert.IsFalse(result.IsComplete);
        Assert.AreEqual(1, result.TotalCount);
    }

    private sealed class StubHandler(HttpStatusCode status, string body, Uri? next = null) : HttpMessageHandler
    {
        public Uri? Url { get; private set; }

        public HttpMethod? Method { get; private set; }

        public string? Authorization { get; private set; }

        public string? Accept { get; private set; }

        public string? ApiVersion { get; private set; }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Url = request.RequestUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            Accept = request.Headers.Accept.ToString();
            ApiVersion = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (next is not null)
            {
                response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
            }

            return Task.FromResult(response);
        }

    }

    private sealed class CreateCodespaceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _nextResponse;

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri,
                request.Method,
                request.Headers.Authorization?.ToString(),
                body));
            var response = responses[_nextResponse++];
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record CapturedRequest(Uri? Url, HttpMethod Method, string? Authorization, string? Body);

    private sealed class SequenceHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responses[RequestCount++](request));
    }
}
