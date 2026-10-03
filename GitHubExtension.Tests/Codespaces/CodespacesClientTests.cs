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
        using var handler = new StubHandler(HttpStatusCode.OK, CodespaceJson);
        using var http = new HttpClient(handler);

        var codespace = await new CodespacesClient(http).GetCodespaceAsync(Account, "workspace/name", TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace%2Fname"), handler.Url);
        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual("Bearer " + Account.Token, handler.Authorization);
        Assert.AreEqual("application/vnd.github+json", handler.Accept);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
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
