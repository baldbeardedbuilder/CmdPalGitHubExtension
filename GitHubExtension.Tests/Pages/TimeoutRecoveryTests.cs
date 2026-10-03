// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public sealed class TimeoutRecoveryTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private const string TimeoutMessage = "The request to api.github.com timed out. Try again.";
    private const string IssueJson = """{"number":1,"title":"Recovered","state":"open","html_url":"https://github.com/o/r/issues/1"}""";
    private const string PullRequestJson = """{"number":1,"title":"Recovered","state":"open","html_url":"https://github.com/o/r/pull/1","head":{},"base":{}}""";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ListTimeout_ShowsErrorClearsLoadingAndRetries(bool pullRequests)
    {
        var requests = 0;
        using var http = new HttpClient(new StubHandler(_ =>
            ++requests == 1
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport timeout"))
                : Task.FromResult(JsonResponse($"[{(pullRequests ? PullRequestJson : IssueJson)}]"))));
        var auth = CreateAuth();
        var (page, load, open, retry) = CreateList(pullRequests, auth, http);
        using var disposable = (IDisposable)page;

        open();
        await load();

        Assert.IsFalse(page.IsLoading);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual(TimeoutMessage, page.EmptyContent!.Subtitle);
        Assert.IsInstanceOfType<InvokableCommand>(page.EmptyContent.Command);
        var empty = page.EmptyContent;
        page.GetItems();
        Assert.AreSame(empty, page.EmptyContent);
        Assert.AreEqual(1, requests);

        retry();
        await load();

        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("#1 Recovered", page.GetItems().Single().Title);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ListTimeout_FromPreviousAccountDoesNotOverwriteNewLoad(bool pullRequests)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            if (++requests == 1)
            {
                started.SetResult();
                return pending.Task;
            }

            return Task.FromResult(JsonResponse($"[{(pullRequests ? PullRequestJson : IssueJson)}]"));
        }));
        var auth = CreateAuth();
        var (page, load, open, _) = CreateList(pullRequests, auth, http);
        using var disposable = (IDisposable)page;
        open();
        var oldLoad = load();
        await started.Task.WaitAsync(TestContext.CancellationToken);

        await auth.SignInWithTokenAsync("https://github.example.com", "another-test-token", TestContext.CancellationToken);
        open();
        await load();
        pending.SetException(new TaskCanceledException("old transport timeout"));
        await oldLoad;

        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("#1 Recovered", page.GetItems().Single().Title);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    public async Task IssueDetailTimeout_ShowsRetryAndRecovers()
    {
        var requests = 0;
        using var http = new HttpClient(new StubHandler(_ =>
            ++requests == 1
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport timeout"))
                : Task.FromResult(JsonResponse(IssueJson))));
        var page = new IssueDetailsPage(CreateAuth(), new IssuesClient(http), new FakeBrowser(_ => null));
        page.LoadIssue(Account, new Uri("https://api.github.com/repos/o/r/issues/1"), "o/r");
        await page.CurrentLoad;

        Assert.IsFalse(page.IsLoading);
        var error = Assert.IsInstanceOfType<FormContent>(page.GetContent().Single()).TemplateJson;
        Assert.Contains(TimeoutMessage, error);
        Assert.Contains(IssueDetailsActions.Retry, error);

        page.HandleSubmit(IssueDetailsActions.Retry);
        await page.CurrentLoad;

        Assert.IsFalse(page.IsLoading);
        Assert.Contains("Recovered", Assert.IsInstanceOfType<FormContent>(page.GetContent().Single()).TemplateJson);
        Assert.AreEqual(2, requests);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepositoryTimeout_ClearsLoadingAndRefreshRetries(bool search)
    {
        var requests = 0;
        var timeoutRequest = search ? 2 : 1;
        const string repository = """{"full_name":"o/recovered","html_url":"https://github.com/o/recovered"}""";
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (Interlocked.Increment(ref requests) == timeoutRequest)
            {
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport timeout"));
            }

            return Task.FromResult(JsonResponse(search
                ? request.RequestUri!.AbsolutePath == "/search/repositories" ? $$"""{"items":[{{repository}}],"total_count":1}""" : "[]"
                : $"[{repository}]"));
        }));
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var page = new ReposPage(auth, new RepositoriesClient(http), browser, issues, pulls, searchDelay: TimeSpan.Zero);
        page.GetItems();
        await page.CurrentLoad;
        if (search)
        {
            page.SearchText = "recovered";
            await page.CurrentSearch;
        }

        Assert.IsFalse(page.IsLoading);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual(TimeoutMessage, page.EmptyContent!.Subtitle);
        ((InvokableCommand)page.EmptyContent.Command!).Invoke();
        await page.CurrentLoad;
        await page.CurrentSearch;

        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("o/recovered", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task PullRequestDetailTimeout_ShowsErrorAndRefreshRetries()
    {
        const string notifications = """
            [{"id":"1","unread":true,"reason":"review_requested",
              "subject":{"title":"Recovered","type":"PullRequest","url":"https://api.github.com/repos/o/r/pulls/1"},
              "repository":{"full_name":"o/r","html_url":"https://github.com/o/r"},"updated_at":"2025-06-01T12:00:00Z"}]
            """;
        var requests = 0;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            requests++;
            if (requests == 2)
            {
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport timeout"));
            }

            return Task.FromResult(JsonResponse(requests is 1 or 3 ? notifications : PullRequestJson));
        }));
        var page = new NotificationsPage(CreateAuth(), new NotificationsClient(http), new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual(TimeoutMessage, page.GetItems().Single().Details!.Body);

        await page.RefreshAsync();

        Assert.IsFalse(page.IsLoading);
        Assert.IsInstanceOfType<PullRequestDetails>(page.GetItems().Single().Details);
        Assert.AreNotEqual(TimeoutMessage, page.GetItems().Single().Details!.Body);
        Assert.AreEqual(4, requests);
    }

    private static (DynamicListPage Page, Func<Task> Load, Action Open, Action Retry) CreateList(
        bool pullRequests, AuthService auth, HttpClient http)
    {
        var browser = new FakeBrowser(_ => null);
        if (pullRequests)
        {
            var page = new RepositoryPullRequestsPage(auth, new PullRequestsClient(http), browser);
            return (page, () => page.CurrentLoad, () => page.Open("o/r"),
                () => ((InvokableCommand)page.EmptyContent!.Command!).Invoke());
        }

        var issues = new RepositoryIssuesPage(auth, new IssuesClient(http), browser);
        return (issues, () => issues.CurrentLoad, () => issues.Open("o/r"),
            () => ((InvokableCommand)issues.EmptyContent!.Command!).Invoke());
    }

    private static AuthService CreateAuth()
    {
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mona");
        return new AuthService(new InMemoryAccountStore(Account), client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    public TestContext TestContext { get; set; } = null!;
}
