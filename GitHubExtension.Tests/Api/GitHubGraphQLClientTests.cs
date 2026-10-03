// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Api;

[TestClass]
public sealed class GitHubGraphQLClientTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");

    [TestMethod]
    [DataRow("github.com", "https://api.github.com/graphql")]
    [DataRow("octocorp.ghe.com", "https://api.octocorp.ghe.com/graphql")]
    [DataRow("github.example.com", "https://github.example.com/api/graphql")]
    [DataRow("https://github.example.com:8443", "https://github.example.com:8443/api/graphql")]
    public async Task ExecuteAsync_UsesHostEndpointAuthenticationAndJsonVariables(string hostName, string endpoint)
    {
        Assert.IsTrue(GitHubHost.TryParse(hostName, out var host));
        var account = Account with { Host = host };
        const string query = "mutation Draft($id: ID!) { convertPullRequestToDraft(input: {pullRequestId: $id}) { clientMutationId } }";
        using var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual(new Uri(endpoint), request.RequestUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual(account.Token, request.Headers.Authorization?.Parameter);
            Assert.IsNotEmpty(request.Headers.UserAgent);
            Assert.IsNotNull(request.Content);
            Assert.AreEqual("application/json", request.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            Assert.AreEqual(query, body.RootElement.GetProperty("query").GetString());
            Assert.AreEqual("Draft", body.RootElement.GetProperty("operationName").GetString());
            Assert.AreEqual("opaque\"node\nid", body.RootElement.GetProperty("variables").GetProperty("id").GetString());
            return JsonResponse("""{"data":{"convertPullRequestToDraft":{"clientMutationId":null}}}""");
        }));

        var result = await new GitHubGraphQLClient(http).ExecuteAsync(
            account, query, new { id = "opaque\"node\nid" }, TestContext.CancellationToken, "Draft");

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(result.HasPartialData);
        Assert.IsNotNull(result.Data);
        Assert.IsEmpty(result.Errors);
    }

    [TestMethod]
    public async Task ExecuteAsync_HttpSuccessRetainsApiErrorsAndPartialDataAfterDisposal()
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse("""
            {
              "data":{"repository":{"pullRequest":{"id":"PR_node"},"discussion":null}},
              "errors":[{
                "message":"Discussion is not accessible",
                "type":"FORBIDDEN",
                "path":["repository","discussion",0],
                "locations":[{"line":3,"column":5}],
                "extensions":{"code":"FORBIDDEN"}
              }]
            }
            """))));

        var result = await new GitHubGraphQLClient(http).ExecuteAsync(
            Account, "query { repository { pullRequest { id } discussion { id } } }", null, TestContext.CancellationToken);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsTrue(result.HasPartialData);
        Assert.AreEqual("PR_node", result.Data!.Value.GetProperty("repository").GetProperty("pullRequest").GetProperty("id").GetString());
        var error = Assert.ContainsSingle(result.Errors);
        Assert.AreEqual("Discussion is not accessible", error.Message);
        Assert.AreEqual("FORBIDDEN", error.Type);
        Assert.AreEqual("repository", error.Path!.Value[0].GetString());
        Assert.AreEqual(0, error.Path.Value[2].GetInt32());
        Assert.AreEqual(3, error.Locations!.Value[0].GetProperty("line").GetInt32());
        Assert.AreEqual("FORBIDDEN", error.Extensions!.Value.GetProperty("code").GetString());
        Assert.IsNull(error.UnsupportedField);
    }

    [TestMethod]
    [DataRow("""{"errors":[{"message":"Bad query"}]}""")]
    [DataRow("""{"data":null,"errors":[{"message":"Bad query"}]}""")]
    public async Task ExecuteAsync_ErrorsWithoutDataAreNotSuccess(string payload)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse(payload))));

        var result = await new GitHubGraphQLClient(http).ExecuteAsync(Account, "query { viewer { id } }", null, TestContext.CancellationToken);

        Assert.IsFalse(result.IsSuccess);
        Assert.IsFalse(result.HasPartialData);
        Assert.IsNull(result.Data);
        Assert.AreEqual("Bad query", Assert.ContainsSingle(result.Errors).Message);
    }

    [TestMethod]
    public async Task GetNodeIdAsync_UnsupportedDiscussionFieldDoesNotDisablePullRequests()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        var account = Account with { Host = host };
        using var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            return body.RootElement.GetProperty("query").GetString()!.Contains("discussion(", StringComparison.Ordinal)
                ? JsonResponse("""
                    {"errors":[{"message":"Field 'discussion' doesn't exist on type 'Repository'",
                    "path":["query NodeId","repository","discussion"],
                    "extensions":{"code":"undefinedField","typeName":"Repository","fieldName":"discussion"}}]}
                    """)
                : JsonResponse("""{"data":{"repository":{"pullRequest":{"id":"PR_supported"}}}}""");
        }));
        var client = new GitHubGraphQLClient(http);

        var discussion = await client.GetNodeIdAsync(account, "o/r", 7, GitHubNodeKind.Discussion, TestContext.CancellationToken);
        var pullRequest = await client.GetNodeIdAsync(account, "o/r", 7, GitHubNodeKind.PullRequest, TestContext.CancellationToken);

        Assert.IsNull(discussion.NodeId);
        Assert.IsFalse(discussion.Response.IsSuccess);
        Assert.AreEqual(new GraphQLField("Repository", "discussion"), Assert.ContainsSingle(discussion.Response.Errors).UnsupportedField);
        Assert.IsTrue(pullRequest.Response.IsSuccess);
        Assert.AreEqual("PR_supported", pullRequest.NodeId);
    }

    [TestMethod]
    public async Task ExecuteAsync_AccountChangesUseCurrentHostAndTokenWithoutCachedResults()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        GitHubAccount[] accounts = [Account, Account with { Login = "other", Token = "other-test-token" }, Account with { Host = host }];
        var call = 0;
        using var http = new HttpClient(new StubHandler((request, _) =>
        {
            var account = accounts[call++];
            Assert.AreEqual(account.Host.GraphQLUrl, request.RequestUri);
            Assert.AreEqual(account.Token, request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { data = new { viewer = new { login = account.Login } } })));
        }));
        var client = new GitHubGraphQLClient(http);

        foreach (var account in accounts)
        {
            var result = await client.ExecuteAsync(account, "query { viewer { login } }", null, TestContext.CancellationToken);
            Assert.AreEqual(account.Login, result.Data!.Value.GetProperty("viewer").GetProperty("login").GetString());
        }

        Assert.AreEqual(accounts.Length, call);
    }

    [TestMethod]
    [DataRow((int)GitHubNodeKind.PullRequest, "pullRequest")]
    [DataRow((int)GitHubNodeKind.Discussion, "discussion")]
    public async Task GetNodeIdAsync_ResolvesOpaqueIdUsingVariables(int kindValue, string field)
    {
        var kind = (GitHubNodeKind)kindValue;
        using var http = new HttpClient(new StubHandler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Contains($"{field}(number: $number) {{ id }}", body.RootElement.GetProperty("query").GetString()!);
            var variables = body.RootElement.GetProperty("variables");
            Assert.AreEqual("o", variables.GetProperty("owner").GetString());
            Assert.AreEqual("r", variables.GetProperty("name").GetString());
            Assert.AreEqual(7, variables.GetProperty("number").GetInt32());
            return JsonResponse(JsonSerializer.Serialize(new
            {
                data = new { repository = new Dictionary<string, object> { [field] = new { id = "opaque-node-id" } } },
            }));
        }));

        var result = await new GitHubGraphQLClient(http).GetNodeIdAsync(Account, "o/r", 7, kind, TestContext.CancellationToken);

        Assert.AreEqual("opaque-node-id", result.NodeId);
        Assert.IsTrue(result.Response.IsSuccess);
    }

    [TestMethod]
    [DataRow("""{"data":{"repository":null}}""")]
    [DataRow("""{"data":{"repository":{"discussion":null}}}""")]
    public async Task GetNodeIdAsync_MissingNodeIsExplicitlyNull(string payload)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse(payload))));

        var result = await new GitHubGraphQLClient(http).GetNodeIdAsync(Account, "o/r", 7, GitHubNodeKind.Discussion, TestContext.CancellationToken);

        Assert.IsNull(result.NodeId);
        Assert.IsTrue(result.Response.IsSuccess);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task ExecuteAsync_HttpFailuresThrowInsteadOfParsingData(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse("""{"data":{"viewer":{}}}""", status))));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new GitHubGraphQLClient(http).ExecuteAsync(Account, "query { viewer { id } }", null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task ExecuteAsync_NetworkFailurePreservesTransportException()
    {
        var failure = new HttpRequestException(HttpRequestError.NameResolutionError, "Test network failure");
        using var http = new HttpClient(new StubHandler((_, _) => throw failure));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new GitHubGraphQLClient(http).ExecuteAsync(Account, "query { viewer { id } }", null, TestContext.CancellationToken));

        Assert.AreSame(failure, error.InnerException);
    }

    [TestMethod]
    public async Task ExecuteAsync_TimeoutIsActionable()
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new TaskCanceledException()));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new GitHubGraphQLClient(http).ExecuteAsync(Account, "query { viewer { id } }", null, TestContext.CancellationToken));

        Assert.AreEqual("The request to api.github.com timed out. Try again.", error.Message);
    }

    [TestMethod]
    public async Task ExecuteAsync_CallerCancellationIsPreserved()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var http = new HttpClient(new StubHandler((_, _) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GitHubGraphQLClient(http).ExecuteAsync(Account, "query { viewer { id } }", null, cancellation.Token));
    }

    [TestMethod]
    [DataRow("invalid-json")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("""{"data":null}""")]
    [DataRow("""{"data":[]}""")]
    [DataRow("""{"errors":{}}""")]
    [DataRow("""{"errors":[]}""")]
    [DataRow("""{"errors":[{}]}""")]
    public async Task ExecuteAsync_MalformedResponsesThrowFriendlyError(string payload)
    {
        using var http = new HttpClient(new StubHandler((_, _) => Task.FromResult(JsonResponse(payload))));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new GitHubGraphQLClient(http).ExecuteAsync(Account, "query { viewer { id } }", null, TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow("o")]
    [DataRow("o/r/extra")]
    [DataRow("/r")]
    public async Task GetNodeIdAsync_InvalidRepositoryDoesNotSendRequest(string repository)
    {
        using var http = new HttpClient(new StubHandler((_, _) => throw new AssertFailedException("Unexpected request")));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new GitHubGraphQLClient(http).GetNodeIdAsync(Account, repository, 7, GitHubNodeKind.PullRequest, TestContext.CancellationToken));
    }

    private static HttpResponseMessage JsonResponse(string payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(payload) };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    public TestContext TestContext { get; set; }
}
