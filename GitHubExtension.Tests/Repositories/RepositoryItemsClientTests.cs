// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public sealed class RepositoryItemsClientTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly SubjectState[] ExpectedPullRequestStates =
        [SubjectState.Open, SubjectState.Draft, SubjectState.Merged, SubjectState.Closed, SubjectState.Unknown];

    [TestMethod]
    public async Task IssuesClient_FiltersPullRequestsAndFollowsNextPage()
    {
        var next = new Uri("https://api.github.com/repos/octo/tool/issues?page=2");
        var requests = new List<Uri>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var content = requests.Count == 1
                ? """
                  [
                    {"number":1,"title":"Issue","state":"open","html_url":"https://github.com/octo/tool/issues/1"},
                    {"number":2,"title":"Pull request","state":"open","html_url":"https://github.com/octo/tool/pull/2","pull_request":{"url":"https://api.github.com/repos/octo/tool/pulls/2"}}
                  ]
                  """
                : """[{"number":3,"title":"Second page","state":"closed","html_url":"https://github.com/octo/tool/issues/3"}]""";
            var response = JsonResponse(content);
            if (requests.Count == 1)
            {
                response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
            }

            return response;
        }));
        var client = new IssuesClient(http);

        var first = await client.GetIssuesAsync(Account, "octo/tool", null, TestContext.CancellationToken);
        var second = await client.GetIssuesAsync(Account, "octo/tool", first.NextPage, TestContext.CancellationToken);

        Assert.AreEqual(1, first.Issues.Count);
        Assert.AreEqual("Issue", first.Issues[0].Title);
        Assert.AreEqual(next, first.NextPage);
        Assert.AreEqual(1, second.Issues.Count);
        Assert.AreEqual("Second page", second.Issues[0].Title);
        Assert.IsNull(second.NextPage);
        Assert.AreEqual(
            $"https://api.github.com/repos/octo/tool/issues?state=all&sort=created&direction=desc&per_page={IssuesClient.PageSize}",
            requests[0].AbsoluteUri);
        Assert.AreEqual(next, requests[1]);
    }

    [TestMethod]
    public async Task PullRequestsClient_ParsesStatesAndFollowsNextPage()
    {
        var next = new Uri("https://api.github.com/repos/octo/tool/pulls?page=2");
        var requests = new List<Uri>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Add(request.RequestUri!);
            var content = requests.Count == 1
                ? """
                  [
                    {"number":1,"title":"Open PR","state":"open","html_url":"https://github.com/octo/tool/pull/1","draft":false,"head":{"label":"contributor:feature","ref":"feature"},"base":{"label":"octo:main","ref":"main"}},
                    {"number":2,"title":"Draft PR","state":"open","html_url":"https://github.com/octo/tool/pull/2","draft":true,"head":{"ref":"draft"},"base":{"ref":"main"}},
                    {"number":3,"title":"Merged PR","state":"closed","merged":true,"html_url":"https://github.com/octo/tool/pull/3","head":{},"base":{}},
                    {"number":4,"title":"Closed PR","state":"closed","merged":false,"html_url":"https://github.com/octo/tool/pull/4","head":{},"base":{}},
                    {"number":5,"title":"Unknown PR","state":"custom","html_url":"https://github.com/octo/tool/pull/5","head":{},"base":{}}
                  ]
                  """
                : """[{"number":5,"title":"Next page","state":"open","html_url":"https://github.com/octo/tool/pull/5","head":{},"base":{}}]""";
            var response = JsonResponse(content);
            if (requests.Count == 1)
            {
                response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
            }

            return response;
        }));
        var client = new PullRequestsClient(http);

        var first = await client.GetPullRequestsAsync(Account, "octo/tool", null, TestContext.CancellationToken);
        var second = await client.GetPullRequestsAsync(Account, "octo/tool", first.NextPage, TestContext.CancellationToken);

        CollectionAssert.AreEqual(
            ExpectedPullRequestStates,
            first.PullRequests.Select(pullRequest => pullRequest.State).ToArray());
        Assert.AreEqual("contributor:feature", first.PullRequests[0].HeadBranch);
        Assert.AreEqual("octo:main", first.PullRequests[0].BaseBranch);
        Assert.AreEqual("feature", first.PullRequests[0].HeadRef);
        Assert.AreEqual("main", first.PullRequests[0].BaseRef);
        Assert.AreEqual(next, first.NextPage);
        Assert.AreEqual("Next page", second.PullRequests.Single().Title);
        Assert.IsNull(second.NextPage);
        Assert.AreEqual(
            $"https://api.github.com/repos/octo/tool/pulls?state=all&sort=created&direction=desc&per_page={PullRequestsClient.PageSize}",
            requests[0].AbsoluteUri);
        Assert.AreEqual(next, requests[1]);
    }

    [TestMethod]
    public void IssuesClient_RejectsUnexpectedListPayload()
    {
        using var json = JsonDocument.Parse("""{"message":"not an issue list"}""");

        var error = Assert.ThrowsExactly<GitHubApiException>(() => IssuesClient.ParseIssues(json.RootElement));

        Assert.AreEqual("GitHub sent back an issue list we couldn't read.", error.Message);
    }

    [TestMethod]
    public void IssuesClient_RejectsUnexpectedListEntry()
    {
        using var json = JsonDocument.Parse("""[null]""");

        var error = Assert.ThrowsExactly<GitHubApiException>(() => IssuesClient.ParseIssues(json.RootElement));

        Assert.AreEqual("GitHub sent back an issue we couldn't read.", error.Message);
    }

    [TestMethod]
    public void PullRequestsClient_RejectsUnexpectedListPayload()
    {
        using var json = JsonDocument.Parse("""{"message":"not a pull request list"}""");

        var error = Assert.ThrowsExactly<GitHubApiException>(() => PullRequestsClient.ParsePullRequests(json.RootElement));

        Assert.AreEqual("GitHub sent back a pull request list we couldn't read.", error.Message);
    }

    [TestMethod]
    public void PullRequestsClient_RejectsUnexpectedListEntry()
    {
        using var json = JsonDocument.Parse("""[null]""");

        var error = Assert.ThrowsExactly<GitHubApiException>(() => PullRequestsClient.ParsePullRequests(json.RootElement));

        Assert.AreEqual("GitHub sent back a pull request we couldn't read.", error.Message);
    }

    public TestContext TestContext { get; set; } = null!;

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
