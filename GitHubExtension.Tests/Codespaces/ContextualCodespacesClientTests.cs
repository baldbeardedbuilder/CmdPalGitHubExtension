// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public class ContextualCodespacesClientTests
{
    private const string SpaceJson = """
        {"name":"workspace","display_name":"Workspace","repository":{"full_name":"octocat/hello"},
        "git_status":{"ref":"feature/pr-7"},"state":"Available","web_url":"https://workspace.github.dev"}
        """;
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "token");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetRepositoryCodespacesAsync_UsesRepositoryScopedListAndPagination()
    {
        var next = new Uri("https://api.github.com/repos/octocat/hello/codespaces?per_page=50&page=2");
        using var handler = new Handler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"total_count":1,"codespaces":[{{SpaceJson}}]}"""),
            };
            if (request.RequestUri!.Query.EndsWith("per_page=50", StringComparison.Ordinal))
            {
                response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
            }

            return response;
        });
        using var http = new HttpClient(handler);
        var client = new CodespacesClient(http);

        var result = await client.GetRepositoryCodespacesAsync(Account, "octocat/hello", null, TestContext.CancellationToken);
        await client.GetRepositoryCodespacesAsync(Account, "octocat/hello", result.NextPage, TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/repos/octocat/hello/codespaces?per_page=50"), handler.Requests[0].Uri);
        Assert.AreEqual(next, handler.Requests[1].Uri);
        Assert.AreEqual("Bearer " + Account.Token, handler.Requests[0].Authorization);
        Assert.AreEqual("workspace", result.Codespaces.Single().Name);
    }

    [TestMethod]
    public async Task CreateRepositoryCodespaceAsync_UsesRepositoryEndpointAndVerifiesTheCreatedContext()
    {
        using var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(SpaceJson) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SpaceJson) };
        });
        using var http = new HttpClient(handler);

        var result = await new CodespacesClient(http).CreateRepositoryCodespaceAsync(
            Account, "octocat/hello", "feature/pr-7", TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/repos/octocat/hello/codespaces"), handler.Requests[0].Uri);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        using var request = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.AreEqual("feature/pr-7", request.RootElement.GetProperty("ref").GetString());
        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace"), handler.Requests[1].Uri);
        Assert.AreEqual("octocat/hello", result.RepositoryFullName);
    }

    [TestMethod]
    public async Task CreatePullRequestCodespaceAsync_UsesPullRequestEndpointAndExpectedHeadRef()
    {
        using var handler = new Handler((request, _) => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(SpaceJson) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SpaceJson) });
        using var http = new HttpClient(handler);

        var result = await new CodespacesClient(http).CreatePullRequestCodespaceAsync(
            Account, "octocat/hello", 7, "feature/pr-7", TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/repos/octocat/hello/pulls/7/codespaces"), handler.Requests[0].Uri);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[0].Method);
        Assert.AreEqual("Bearer " + Account.Token, handler.Requests[0].Authorization);
        Assert.AreEqual(new Uri("https://api.github.com/user/codespaces/workspace"), handler.Requests[1].Uri);
        Assert.AreEqual("feature/pr-7", result.Branch);
    }

    [TestMethod]
    public async Task CreatePullRequestCodespaceAsync_RejectsUnexpectedHeadAfterCreation()
    {
        var unexpected = SpaceJson.Replace("feature/pr-7", "main", StringComparison.Ordinal);
        using var handler = new Handler((request, _) => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(SpaceJson) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(unexpected) });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreatePullRequestCodespaceAsync(
                Account, "octocat/hello", 7, "feature/pr-7", TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        StringAssert.Contains(error.Message, "doesn't match");
    }

    [TestMethod]
    public async Task ContextualCodespaces_RejectEnterpriseBeforeCallingGitHub()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new CodespacesClient(http).CreatePullRequestCodespaceAsync(
            new GitHubAccount(host!, "octocat", "token"), "octocat/hello", 7, null, TestContext.CancellationToken));

        Assert.IsEmpty(handler.Requests);
    }

    private sealed record Request(Uri? Uri, HttpMethod Method, string? Authorization, string? Body);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Request(request.RequestUri, request.Method, request.Headers.Authorization?.ToString(), body));
            return respond(request, cancellationToken);
        }
    }
}
