// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Actions;

[TestClass]
public class ActionsViewTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] RepositorySections = ["o/r", "Issues", "Pull Requests", "Actions", "Discussions"];
    private const string RunJson = """
        {"workflow_runs":[{"id":9876543210,"name":"CI","display_title":"Fix palette flicker",
        "actor":{"login":"mona"},"status":"completed","conclusion":"failure",
        "created_at":"2025-06-01T11:48:00Z","html_url":"https://github.com/o/r/actions/runs/9876543210"}]}
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RepoMenu_OpensRepositoryAndActionsPages()
    {
        var auth = Auth();
        var repos = new Mock<IRepositoriesClient>();
        repos.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult(
                [new GitHubRepository("o/r", new Uri("https://github.com/o/r"), null, false, false, false, null, 0, 0, Now, null)], null));
        var client = Client([Run()]);
        var browser = new FakeBrowser(_ => null);
        using var provider = new GitHubCommandsProvider(auth, () => string.Empty, browser: browser, repositoriesClient: repos.Object, actionsClient: client.Object);
        var page = (ReposPage)provider.GetCommand(ReposPage.PageId)!;
        page.GetItems();
        await page.CurrentLoad;
        var item = page.GetItems().Single();
        var repository = Assert.IsInstanceOfType<RepositoryPage>(item.Command);
        Assert.AreEqual("o/r", repository.Title);
        Assert.IsNull(browser.LastOpened);
        CollectionAssert.AreEqual(RepositorySections, repository.GetItems().Select(section => section.Title).ToArray());
        var command = repository.GetItems().Single(i => i.Title == "Actions").Command;

        Assert.IsInstanceOfType<OpenActionsCommand>(command).Invoke();
        var actions = (ActionsPage)provider.GetCommand(ActionsPage.PageId)!;
        actions.GetItems();
        await actions.CurrentLoad;

        Assert.AreEqual("CI", actions.GetItems().Single().Title);
        client.Verify(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsInstanceOfType<OpenActionsCommand>(item.MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is OpenActionsCommand).Command);
        auth.SignOut();
        Assert.IsEmpty(repository.GetItems());
        Assert.AreEqual("Repository", repository.Title);
    }

    [TestMethod]
    public async Task LoadsScreenshotMetadataAndOpensRun()
    {
        var browser = new FakeBrowser(_ => null);
        using var page = Page(Client([Run()]).Object, browser: browser);
        page.OpenRepository("o/r");
        Assert.AreEqual("Actions", page.Title);
        Assert.AreEqual("Filter workflow runs...", page.PlaceholderText);
        page.GetItems();
        await page.CurrentLoad;
        var item = (WorkflowRunItem)page.GetItems().Single();

        Assert.AreEqual("CI", item.Title);
        Assert.AreEqual("Fix palette flicker \u00B7 mona \u00B7 12m ago", item.Subtitle);
        Assert.AreSame(Icons.RunSuccess, item.Icon);
        ((InvokableCommand)item.Command!).Invoke();
        Assert.AreEqual(Run().WebUrl, browser.LastOpened);
        Assert.IsTrue(item.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is RefreshActionsCommand));
        Assert.IsTrue(item.MoreCommands.OfType<CommandContextItem>().Any(c => c.Title == "Status: Success"));
    }

    [TestMethod]
    [DataRow("completed", "success", "Success")]
    [DataRow("completed", "failure", "Failure")]
    [DataRow("completed", "timed_out", "Timed out")]
    [DataRow("completed", "cancelled", "Cancelled")]
    [DataRow("completed", "skipped", "Skipped")]
    [DataRow("completed", "neutral", "Neutral")]
    [DataRow("completed", "action_required", "Action required")]
    [DataRow("completed", "stale", "Stale")]
    [DataRow("completed", null, "Unknown")]
    [DataRow("in_progress", null, "In progress")]
    [DataRow("queued", null, "Queued")]
    [DataRow("requested", null, "Queued")]
    [DataRow("waiting", null, "Queued")]
    [DataRow("pending", null, "Queued")]
    [DataRow("future_status", null, "Unknown")]
    public void State_UsesStatusAndConclusion(string status, string? conclusion, string expected)
    {
        var run = Run() with { Status = status, Conclusion = conclusion };

        Assert.AreEqual(expected, WorkflowRunFormatting.State(run));
        var icon = expected switch
        {
            "Success" => Icons.RunSuccess,
            "Failure" or "Timed out" => Icons.RunFailure,
            "In progress" => Icons.RunInProgress,
            _ => Icons.RunNeutral,
        };
        Assert.AreSame(icon, WorkflowRunFormatting.Icon(run));
    }

    [TestMethod]
    [DataRow("ci")]
    [DataRow("palette MONA")]
    [DataRow("success")]
    public async Task Filter_MatchesWorkflowTitleActorAndStatus(string query)
    {
        using var page = await Loaded(Client([Run(), Run(2) with { Name = "Release", DisplayTitle = "v1", Actor = "bob", Conclusion = "failure" }]).Object);
        page.SearchText = query;

        Assert.AreEqual(1L, ((WorkflowRunItem)page.GetItems().Single()).Run.Id);
        page.SearchText = "not found";
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No workflow runs found", page.EmptyContent!.Title);
        page.SearchText = string.Empty;
        Assert.HasCount(2, page.GetItems());
    }

    [TestMethod]
    public async Task LoadMore_AppendsAndDeduplicatesRuns()
    {
        var next = new Uri("https://api.github.com/repos/o/r/actions/runs?page=2");
        var client = Client([Run()], next);
        client.Setup(c => c.GetRunsAsync(Account, "o/r", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([Run(), Run(2)], null));
        using var page = await Loaded(client.Object);
        Assert.IsTrue(page.HasMoreItems);

        page.LoadMore();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(new long[] { 1, 2 }, page.GetItems().Cast<WorkflowRunItem>().Select(i => i.Run.Id).ToArray());
        Assert.IsFalse(page.HasMoreItems);
        page.LoadMore();
        client.Verify(c => c.GetRunsAsync(Account, "o/r", next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Refresh_ReplacesRunsAndPreservesFilter()
    {
        var client = Client([Run()]);
        using var page = await Loaded(client.Object);
        page.SearchText = "release";
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([Run(2) with { Name = "Release" }], null));

        await page.RefreshAsync();

        Assert.AreEqual(2L, ((WorkflowRunItem)page.GetItems().Single()).Run.Id);
        Assert.AreEqual("release", page.SearchText);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task EmptyList_HasRefreshCommand()
    {
        using var page = await Loaded(Client([]).Object);

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No workflow runs yet", page.EmptyContent!.Title);
        Assert.IsInstanceOfType<RefreshActionsCommand>(page.EmptyContent.Command);
    }

    [TestMethod]
    public async Task Failure_ShowsErrorAndCanRetry()
    {
        var client = new Mock<IActionsClient>();
        client.SetupSequence(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"))
            .ReturnsAsync(new WorkflowRunsPageResult([Run()], null));
        using var page = await Loaded(client.Object);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("rate limited", page.EmptyContent!.Subtitle);
        Assert.IsInstanceOfType<RefreshActionsCommand>(page.EmptyContent.Command);

        ((RefreshActionsCommand)page.EmptyContent.Command!).Invoke();
        await page.CurrentLoad;

        Assert.AreEqual("CI", page.GetItems().Single().Title);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task NextPageFailure_KeepsRunsAndShowsRetryEvenWhenFiltered()
    {
        var next = new Uri("https://api.github.com/repos/o/r/actions/runs?page=2");
        var client = Client([Run()], next);
        client.Setup(c => c.GetRunsAsync(Account, "o/r", next, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("offline"));
        using var page = await Loaded(client.Object);
        page.LoadMore();
        await page.CurrentLoad;
        Assert.AreEqual("CI", page.GetItems()[0].Title);
        page.SearchText = "not found";

        var retry = page.GetItems().Single();
        Assert.AreEqual("offline", retry.Subtitle);
        Assert.IsInstanceOfType<RefreshActionsCommand>(retry.Command);
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task SwitchingRepositories_IgnoresOldResponseAndClearsSearch()
    {
        var first = new TaskCompletionSource<WorkflowRunsPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<WorkflowRunsPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>())).Returns(first.Task);
        client.Setup(c => c.GetRunsAsync(Account, "o/other", null, It.IsAny<CancellationToken>())).Returns(second.Task);
        using var page = Page(client.Object);
        page.OpenRepository("o/r");
        page.GetItems();
        var oldLoad = page.CurrentLoad;
        page.SearchText = "ci";
        page.OpenRepository("o/other");
        page.GetItems();

        first.SetResult(new WorkflowRunsPageResult([Run()], new Uri("https://api.github.com/stale")));
        await oldLoad;
        Assert.IsTrue(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual(string.Empty, page.SearchText);
        second.SetResult(new WorkflowRunsPageResult([Run(2) with { Name = "Release" }], null));
        await page.CurrentLoad;
        Assert.AreEqual("Release", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task SignOut_DiscardsInflightResponse()
    {
        var response = new TaskCompletionSource<WorkflowRunsPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>())).Returns(response.Task);
        var auth = Auth();
        using var page = Page(client.Object, auth);
        page.OpenRepository("o/r");
        page.GetItems();
        var load = page.CurrentLoad;

        auth.SignOut();
        response.SetResult(new WorkflowRunsPageResult([Run()], null));
        await load;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Sign in to view workflow runs", page.EmptyContent!.Title);
        Assert.IsFalse(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public void ParseRuns_ReadsMetadataAndLargeId()
    {
        using var json = JsonDocument.Parse(RunJson);

        var run = ActionsClient.ParseRuns(json.RootElement).Single();

        Assert.AreEqual(9876543210L, run.Id);
        Assert.AreEqual("CI", run.Name);
        Assert.AreEqual("Fix palette flicker", run.DisplayTitle);
        Assert.AreEqual("mona", run.Actor);
        Assert.AreEqual("completed", run.Status);
        Assert.AreEqual("failure", run.Conclusion);
        Assert.AreEqual(Now.AddMinutes(-12), run.CreatedAt);
        Assert.AreEqual(new Uri("https://github.com/o/r/actions/runs/9876543210"), run.WebUrl);
    }

    [TestMethod]
    public void ParseRuns_HandlesMissingOptionalMetadataAndEmptyList()
    {
        using var json = JsonDocument.Parse("""{"workflow_runs":[{"id":1,"name":null,"actor":null,"html_url":"https://github.com/o/r/actions/runs/1"}]}""");
        var run = ActionsClient.ParseRuns(json.RootElement).Single();
        Assert.AreEqual("Workflow", run.Name);
        Assert.AreEqual("Workflow run", run.DisplayTitle);
        Assert.AreEqual(string.Empty, run.Actor);
        Assert.AreEqual("unknown", run.Status);
        Assert.IsNull(run.Conclusion);
        Assert.AreEqual("Workflow run", WorkflowRunFormatting.Subtitle(run, Now));
        using var empty = JsonDocument.Parse("""{"workflow_runs":[]}""");
        Assert.IsEmpty(ActionsClient.ParseRuns(empty.RootElement));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"workflow_runs":null}""")]
    [DataRow("""{"workflow_runs":[{"id":"broken"}]}""")]
    [DataRow("""{"workflow_runs":[{"id":1}]}""")]
    public void ParseRuns_RejectsMalformedResponses(string value)
    {
        using var json = JsonDocument.Parse(value);

        Assert.Throws<GitHubApiException>(() => ActionsClient.ParseRuns(json.RootElement));
    }

    [TestMethod]
    [DataRow("github.com", "https://api.github.com/repos/o/r/actions/runs?per_page=50")]
    [DataRow("github.example.com", "https://github.example.com/api/v3/repos/o/r/actions/runs?per_page=50")]
    public async Task Client_UsesHostAuthAndPagination(string hostName, string expected)
    {
        Assert.IsTrue(GitHubHost.TryParse(hostName, out var host));
        var account = new GitHubAccount(host!, "octocat", "t");
        var next = new Uri(host!.ApiUrl, "repos/o/r/actions/runs?page=2");
        var handler = new Handler(HttpStatusCode.OK, RunJson, $"<{next}>; rel=\"next\"");
        using var http = new HttpClient(handler);
        var client = new ActionsClient(http);

        var result = await client.GetRunsAsync(account, "o/r", null, TestContext.CancellationToken);

        Assert.AreEqual(new Uri(expected), handler.Uri);
        Assert.AreEqual("Bearer t", handler.Authorization);
        Assert.AreEqual(next, result.NextPage);
        Assert.AreEqual(9876543210L, result.Runs.Single().Id);
        await client.GetRunsAsync(account, "o/r", next, TestContext.CancellationToken);
        Assert.AreEqual(next, handler.Uri);
        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRunsAsync(account, "o/r", new Uri("https://evil.example/runs"), TestContext.CancellationToken));
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "{}")]
    [DataRow(HttpStatusCode.NotFound, "{}")]
    [DataRow(HttpStatusCode.OK, "not json")]
    public async Task Client_SurfacesApiErrors(HttpStatusCode status, string body)
    {
        using var http = new HttpClient(new Handler(status, body));
        var client = new ActionsClient(http);

        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRunsAsync(Account, "o/r", null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Client_ReportsTimeoutButPreservesCallerCancellation()
    {
        using var http = new HttpClient(new TimeoutHandler());
        var client = new ActionsClient(http);

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetRunsAsync(Account, "o/r", null, TestContext.CancellationToken));
        Assert.AreEqual("GitHub took too long to return workflow runs. Try refreshing.", error.Message);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetRunsAsync(Account, "o/r", null, cancellation.Token));
    }

    private static GitHubWorkflowRun Run(long id = 1) =>
        new(id, "CI", "Fix palette flicker", "mona", "completed", "success", Now.AddMinutes(-12), new Uri($"https://github.com/o/r/actions/runs/{id}"));

    private static AuthService Auth() =>
        new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static ActionsPage Page(IActionsClient client, AuthService? auth = null, FakeBrowser? browser = null)
    {
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.GetUtcNow()).Returns(Now);
        return new ActionsPage(auth ?? Auth(), client, browser ?? new FakeBrowser(_ => null), time.Object);
    }

    private static Mock<IActionsClient> Client(GitHubWorkflowRun[] runs, Uri? next = null)
    {
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult(runs, next));
        return client;
    }

    private static async Task<ActionsPage> Loaded(IActionsClient client)
    {
        var page = Page(client);
        page.OpenRepository("o/r");
        page.GetItems();
        await page.CurrentLoad;
        return page;
    }

    private sealed class Handler(HttpStatusCode status, string body, string? link = null) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (link is not null)
            {
                response.Headers.Add("Link", link);
            }

            return Task.FromResult(response);
        }

    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException());
    }
}
