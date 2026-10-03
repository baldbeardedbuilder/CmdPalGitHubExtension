// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public sealed class RepositorySearchClientTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");

    [TestMethod]
    public async Task Search_FollowsContinuationAndReturnsMetadata()
    {
        var requests = new List<Uri>();
        const string query = "repo language:C#";
        var next = new Uri("https://api.github.com/search/repositories?q=repo%20language%3AC%23&per_page=30&page=2");
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return Response(1, 2, requests.Count == 1 ? next : null);
        }));
        var client = new RepositoriesClient(http);

        var first = await client.SearchAsync(Account, query, null, TestContext.CancellationToken);
        var second = await client.SearchAsync(Account, query, first.NextPage, TestContext.CancellationToken);

        Assert.AreEqual(next, first.NextPage);
        Assert.AreEqual(2, first.TotalCount);
        Assert.HasCount(1, first.Repositories);
        Assert.IsNull(second.NextPage);
        Assert.AreEqual(2, second.TotalCount);
        Assert.AreEqual("https://api.github.com/search/repositories?q=repo%20language%3AC%23&per_page=30", requests[0].AbsoluteUri);
        Assert.AreEqual(next, requests[1]);
        Assert.HasCount(2, requests);
    }

    [TestMethod]
    [DataRow(33, 1000, 30, true)]
    [DataRow(34, 1000, 10, false)]
    [DataRow(34, 1001, 10, false)]
    public async Task Search_EnforcesExactAccessibleResultBoundary(int page, int total, int expectedCount, bool hasNext)
    {
        var next = new Uri($"https://api.github.com/search/repositories?q=repo&per_page=30&page={page + 1}");
        using var http = new HttpClient(new StubHandler(_ => Response(30, total, next)));
        var client = new RepositoriesClient(http);
        var uri = new Uri($"https://api.github.com/search/repositories?q=repo&per_page=30&page={page}");

        var result = await client.SearchAsync(Account, "repo", uri, TestContext.CancellationToken);

        Assert.HasCount(expectedCount, result.Repositories);
        Assert.AreEqual(total, result.TotalCount);
        Assert.AreEqual(hasNext ? next : null, result.NextPage);
    }

    [TestMethod]
    [DataRow("https://api.github.com/search/repositories?q=repo&per_page=30&page=0")]
    [DataRow("https://api.github.com/search/repositories?q=repo&per_page=30&page=35")]
    [DataRow("https://api.github.com/search/repositories?q=changed&per_page=30&page=2")]
    [DataRow("https://api.github.com/search/repositories?q=repo&per_page=100&page=2")]
    [DataRow("https://api.github.com/user/repos?q=repo&per_page=30&page=2")]
    [DataRow("https://other.example/search/repositories?q=repo&per_page=30&page=2")]
    public async Task Search_RejectsOutOfScopeContinuationBeforeRequest(string next)
    {
        var requests = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            return Response(0, 0, null);
        }));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).SearchAsync(Account, "repo", new Uri(next), TestContext.CancellationToken));

        Assert.AreEqual(0, requests);
    }

    [TestMethod]
    public async Task Search_RejectsNonAdvancingNextLink()
    {
        var next = new Uri("https://api.github.com/search/repositories?q=repo&per_page=30&page=1");
        using var http = new HttpClient(new StubHandler(_ => Response(1, 2, next)));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).SearchAsync(Account, "repo", null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Search_UsesEnterpriseApiBaseForFirstAndNextPages()
    {
        Assert.IsTrue(GitHubHost.TryParse("https://github.example.com", out var host));
        var account = new GitHubAccount(host, "octocat", "test-token");
        var next = new Uri("https://github.example.com/api/v3/search/repositories?q=repo&per_page=30&page=2");
        var requests = new List<Uri>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            return Response(1, 2, requests.Count == 1 ? next : null);
        }));
        var client = new RepositoriesClient(http);

        var first = await client.SearchAsync(account, "repo", null, TestContext.CancellationToken);
        await client.SearchAsync(account, "repo", first.NextPage, TestContext.CancellationToken);

        Assert.AreEqual("https://github.example.com/api/v3/search/repositories?q=repo&per_page=30", requests[0].AbsoluteUri);
        Assert.AreEqual(next, requests[1]);
    }

    public TestContext TestContext { get; set; } = null!;

    private static HttpResponseMessage Response(int count, int total, Uri? next)
    {
        var rows = string.Join(',', Enumerable.Range(1, count).Select(number =>
            $$"""{"full_name":"o/repo{{number}}","html_url":"https://github.com/o/repo{{number}}"}"""));
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"items":[{{rows}}],"total_count":{{total}},"incomplete_results":false}""",
                Encoding.UTF8, "application/json"),
        };
        if (next is not null)
        {
            response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
        }
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
