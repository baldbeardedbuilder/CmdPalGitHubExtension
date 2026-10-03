// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Actions;

[TestClass]
public class ActionsViewTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] RepositorySections = ["o/r", "Issues", "Pull Requests", "Actions", "Start Copilot task", "Discussions"];
    private static readonly string[] ExpectedFilters = ["Running", "Succeeded", "Failed"];
    private static readonly string[] ExpectedCancelEndpoints =
    [
        "GET https://api.github.com/repos/o/r/actions/runs/1",
        "POST https://api.github.com/repos/o/r/actions/runs/1/cancel",
        "POST https://api.github.com/repos/o/r/actions/runs/1/force-cancel",
    ];
    private const string RunJson = """
        {"workflow_runs":[{"id":9876543210,"name":"CI","display_title":"Fix palette flicker",
        "actor":{"login":"mona"},"status":"completed","conclusion":"failure",
        "event":"push","head_branch":"main","head_sha":"abc123","run_number":42,"run_attempt":2,
        "created_at":"2025-06-01T11:48:00Z","updated_at":"2025-06-01T11:50:00Z",
        "html_url":"https://github.com/o/r/actions/runs/9876543210"}]}
        """;
    private const string SingleRunJson = """
        {"id":1,"name":"CI","display_title":"Fix palette flicker","actor":{"login":"mona"},
        "status":"in_progress","html_url":"https://github.com/o/r/actions/runs/1"}
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

        var actions = Assert.IsInstanceOfType<ActionsPage>(command);
        actions.GetItems();
        await actions.CurrentLoad;

        Assert.AreEqual("CI", actions.GetItems().Single().Title);
        client.Verify(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreSame(actions, item.MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is ActionsPage).Command);
        auth.SignOut();
        Assert.IsEmpty(repository.GetItems());
        Assert.AreEqual("Repository", repository.Title);
    }

    [TestMethod]
    public async Task LoadsScreenshotMetadataAndOpensRun()
    {
        var browser = new FakeBrowser(_ => null);
        using var page = Page(Client([Run() with { Status = "completed", Conclusion = "success" }]).Object, browser: browser);
        page.Filters!.CurrentFilterId = ActionFilters.Succeeded;
        page.OpenRepository("o/r");
        Assert.AreEqual("Actions", page.Title);
        Assert.AreEqual("Filter workflow runs...", page.PlaceholderText);
        page.GetItems();
        await page.CurrentLoad;
        var item = (WorkflowRunItem)page.GetItems().Single();

        Assert.AreEqual("CI", item.Title);
        Assert.AreEqual("Fix palette flicker \u00B7 mona \u00B7 12m ago", item.Subtitle);
        Assert.AreSame(Icons.RunSuccess, item.Icon);
        var details = Assert.IsInstanceOfType<WorkflowRunDetails>(item.Details);
        Assert.AreEqual("Fix palette flicker", details.Title);
        Assert.AreEqual("CI", details.Body);
        Assert.AreEqual("Success", Tags(details, "Status").Single().Text);
        Assert.AreEqual("o/r", Text(details, "Repository"));
        Assert.AreEqual("@mona", Text(details, "Actor"));
        Assert.AreEqual("push", Text(details, "Event"));
        Assert.AreEqual("main", Text(details, "Branch"));
        Assert.AreEqual("42", Text(details, "Run number"));
        Assert.AreEqual("2", Text(details, "Attempt"));
        Assert.AreEqual(Now.AddMinutes(-12).ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), Text(details, "Started"));
        Assert.AreEqual(Now.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), Text(details, "Updated"));
        Assert.AreEqual("abc123", Text(details, "Commit"));
        var runLink = Assert.IsInstanceOfType<IDetailsLink>(details.Metadata.Single(m => m.Key == "Workflow run").Data);
        Assert.AreEqual("#42", runLink.Text);
        Assert.AreEqual(new Uri("https://github.com/o/r/actions/runs/1"), runLink.Link);
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
    [DataRow("success mona")]
    public async Task Filter_MatchesWorkflowTitleActorAndStatus(string query)
    {
        using var page = await Loaded(Client([
            Run() with { Status = "completed", Conclusion = "success" },
            Run(2) with { Name = "Release", DisplayTitle = "v1", Actor = "bob", Status = "completed", Conclusion = "success" },
            Run(3) with { Status = "completed", Conclusion = "failure" },
        ]).Object);
        page.Filters!.CurrentFilterId = ActionFilters.Succeeded;
        page.SearchText = query;

        Assert.AreEqual(1L, ((WorkflowRunItem)page.GetItems().Single()).Run.Id);
        page.SearchText = "not found";
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No workflow runs found", page.EmptyContent!.Title);
        page.SearchText = string.Empty;
        Assert.HasCount(2, page.GetItems());
    }

    [TestMethod]
    public async Task Filters_DefaultToRunningAndSwitchGroupsWithIcons()
    {
        var client = Client([
            Run(),
            Run(2) with { Status = "completed", Conclusion = "success" },
            Run(3) with { Status = "completed", Conclusion = "failure" },
        ]);
        using var page = await Loaded(client.Object);
        var filters = Assert.IsInstanceOfType<ActionFilters>(page.Filters);
        var options = filters.GetFilters().Cast<Filter>().ToArray();
        CollectionAssert.AreEqual(ExpectedFilters, options.Select(option => option.Name).ToArray());
        Assert.AreSame(Icons.RunInProgress, options[0].Icon);
        Assert.AreSame(Icons.RunSuccess, options[1].Icon);
        Assert.AreSame(Icons.RunFailure, options[2].Icon);
        Assert.AreEqual(ActionFilters.Running, filters.CurrentFilterId);
        Assert.AreEqual(1L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        var events = 0;
        page.ItemsChanged += (_, _) => events++;

        filters.CurrentFilterId = ActionFilters.Succeeded;
        Assert.AreEqual(2L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        filters.CurrentFilterId = ActionFilters.Failed;
        Assert.AreEqual(3L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        filters.CurrentFilterId = ActionFilters.Running;
        Assert.AreEqual(1L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        Assert.IsGreaterThan(0, events);
        client.Verify(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("in_progress", null, ActionFilters.Running)]
    [DataRow("queued", null, ActionFilters.Running)]
    [DataRow("requested", null, ActionFilters.Running)]
    [DataRow("waiting", null, ActionFilters.Running)]
    [DataRow("pending", null, ActionFilters.Running)]
    [DataRow("completed", "success", ActionFilters.Succeeded)]
    [DataRow("completed", "failure", ActionFilters.Failed)]
    [DataRow("completed", "timed_out", ActionFilters.Failed)]
    [DataRow("completed", "cancelled", ActionFilters.Failed)]
    [DataRow("completed", "skipped", ActionFilters.Failed)]
    [DataRow("completed", "neutral", ActionFilters.Failed)]
    [DataRow("completed", "action_required", ActionFilters.Failed)]
    [DataRow("completed", "stale", ActionFilters.Failed)]
    [DataRow("completed", null, ActionFilters.Failed)]
    [DataRow("completed", "future_conclusion", ActionFilters.Failed)]
    [DataRow("future_status", null, null)]
    public async Task Filters_GroupStatusesAndConclusions(string status, string? conclusion, string? expectedFilter)
    {
        using var page = await Loaded(Client([Run() with { Status = status, Conclusion = conclusion }]).Object);

        foreach (var filter in page.Filters!.GetFilters().Cast<Filter>())
        {
            page.Filters.CurrentFilterId = filter.Id;
            if (filter.Id == expectedFilter)
            {
                Assert.AreEqual(1L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
            }
            else
            {
                Assert.IsEmpty(page.GetItems());
            }
        }
    }

    [TestMethod]
    public async Task Filters_CombineWithSearchAndShowSelectedEmptyState()
    {
        using var page = await Loaded(Client([
            Run(),
            Run(2) with { Name = "Release", Status = "completed", Conclusion = "failure" },
            Run(3) with { Status = "completed", Conclusion = "failure" },
        ]).Object);
        page.SearchText = "release";
        page.Filters!.CurrentFilterId = ActionFilters.Failed;
        Assert.AreEqual(2L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        page.Filters.CurrentFilterId = ActionFilters.Running;
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Nothing matches \"release\"", page.EmptyContent!.Subtitle);
        page.SearchText = string.Empty;
        page.Filters.CurrentFilterId = ActionFilters.Succeeded;
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No succeeded workflow runs. Refresh to check for new runs", page.EmptyContent.Subtitle);
        Assert.IsInstanceOfType<RefreshActionsCommand>(page.EmptyContent.Command);
    }

    [TestMethod]
    public async Task Filters_RepositoryPagesHaveIndependentRunningDefaults()
    {
        using var page = await Loaded(Client([Run()]).Object);
        page.Filters!.CurrentFilterId = ActionFilters.Failed;
        using var repositoryPage = page.ForRepository("o/other");

        Assert.AreEqual(ActionFilters.Running, repositoryPage.Filters!.CurrentFilterId);
        repositoryPage.Filters.CurrentFilterId = ActionFilters.Succeeded;
        Assert.AreEqual(ActionFilters.Failed, page.Filters.CurrentFilterId);
    }

    [TestMethod]
    public async Task LoadMore_AppendsAndDeduplicatesRuns()
    {
        var next = new Uri("https://api.github.com/repos/o/r/actions/runs?page=2");
        var client = Client([Run()], next);
        client.Setup(c => c.GetRunsAsync(Account, "o/r", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([
                Run(), Run(2), Run(3) with { Status = "completed", Conclusion = "success" },
            ], null));
        using var page = await Loaded(client.Object);
        Assert.IsTrue(page.HasMoreItems);

        page.LoadMore();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(new long[] { 1, 2 }, page.GetItems().Cast<WorkflowRunItem>().Select(i => i.Run.Id).ToArray());
        page.Filters!.CurrentFilterId = ActionFilters.Succeeded;
        Assert.AreEqual(3L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        Assert.IsFalse(page.HasMoreItems);
        page.LoadMore();
        client.Verify(c => c.GetRunsAsync(Account, "o/r", next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Refresh_ReplacesRunsAndPreservesFilter()
    {
        var client = Client([Run() with { Status = "completed", Conclusion = "failure" }]);
        using var page = await Loaded(client.Object);
        page.Filters!.CurrentFilterId = ActionFilters.Failed;
        page.SearchText = "release";
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([
                Run(2) with { Name = "Release", Status = "completed", Conclusion = "failure" },
                Run(3) with { Name = "Release" },
            ], null));

        await page.RefreshAsync();

        Assert.AreEqual(2L, ((WorkflowRunItem)page.GetItems().Single()).Run.Id);
        Assert.AreEqual("release", page.SearchText);
        Assert.AreEqual(ActionFilters.Failed, page.Filters.CurrentFilterId);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task EmptyList_HasRefreshCommand()
    {
        using var page = await Loaded(Client([]).Object);

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No workflow runs found", page.EmptyContent!.Title);
        Assert.AreEqual("No running workflow runs. Refresh to check for new runs", page.EmptyContent.Subtitle);
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
    public async Task Filters_ChangedDuringLoadApplyToResponse()
    {
        var response = new TaskCompletionSource<WorkflowRunsPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>())).Returns(response.Task);
        using var page = Page(client.Object);
        page.OpenRepository("o/r");
        page.GetItems();
        page.Filters!.CurrentFilterId = ActionFilters.Succeeded;

        response.SetResult(new WorkflowRunsPageResult([
            Run(), Run(2) with { Status = "completed", Conclusion = "success" },
        ], null));
        await page.CurrentLoad;

        Assert.AreEqual(2L, Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Id);
        Assert.IsFalse(page.IsLoading);
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
        Assert.AreEqual("push", run.Event);
        Assert.AreEqual("main", run.HeadBranch);
        Assert.AreEqual("abc123", run.HeadSha);
        Assert.AreEqual(42, run.RunNumber);
        Assert.AreEqual(2, run.RunAttempt);
        Assert.AreEqual(new DateTimeOffset(2025, 6, 1, 11, 50, 0, TimeSpan.Zero), run.UpdatedAt);
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
    public async Task Client_ReadsRunAndUsesSeparateCancelEndpoints()
    {
        var handler = new RecordingHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SingleRunJson) }
            : new HttpResponseMessage(HttpStatusCode.Accepted));
        using var http = new HttpClient(handler);
        var client = new ActionsClient(http);

        var run = await client.GetRunAsync(Account, "o/r", 1, TestContext.CancellationToken);
        await client.CancelRunAsync(Account, "o/r", 1, force: false, TestContext.CancellationToken);
        await client.CancelRunAsync(Account, "o/r", 1, force: true, TestContext.CancellationToken);

        Assert.AreEqual("in_progress", run.Status);
        CollectionAssert.AreEqual(ExpectedCancelEndpoints, handler.Requests.Select(r => $"{r.Method} {r.Uri}").ToArray());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Conflict)]
    public async Task Client_SurfacesCancelPermissionAndConflictErrors(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(status, "{}"));
        var client = new ActionsClient(http);

        await Assert.ThrowsAsync<GitHubApiException>(() =>
            client.CancelRunAsync(Account, "o/r", 1, force: false, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Cancel_RefreshesUntilTerminalAndDropsCancelActions()
    {
        var client = Client([Run()]);
        client.SetupSequence(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run())
            .ReturnsAsync(Run() with { Status = "completed", Conclusion = "cancelled" });
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var page = await Loaded(client.Object);
        var item = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());
        page.Filters!.CurrentFilterId = ActionFilters.Failed;

        await page.CancelAsync(item);
        await page.CurrentCancellation;

        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()), Times.Once);
        var cancelled = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());
        Assert.AreEqual("Cancelled", WorkflowRunFormatting.State(cancelled.Run));
        Assert.IsFalse(cancelled.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is CancelWorkflowRunCommand));
    }

    [TestMethod]
    public async Task Cancel_DoesNotPostWhenRunAlreadyCompleted()
    {
        var client = Client([Run()]);
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run() with { Status = "completed", Conclusion = "success" });
        using var page = await Loaded(client.Object);
        var item = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());
        page.Filters!.CurrentFilterId = ActionFilters.Succeeded;

        await page.CancelAsync(item);
        await page.CurrentCancellation;

        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.AreEqual("Success", WorkflowRunFormatting.State(((WorkflowRunItem)page.GetItems().Single()).Run));
    }

    [TestMethod]
    public async Task ForceCancel_RequiresConfirmationAndUsesForceEndpoint()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var client = Client([Run()]);
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, long _, CancellationToken token) =>
            {
                var read = Interlocked.Increment(ref reads);
                if (read == 3)
                {
                    waiting.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return read < 5 ? Run() : Run() with { Status = "completed", Conclusion = "cancelled" };
            });
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var page = await Loaded(client.Object);
        var item = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());
        Assert.IsFalse(item.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is ForceCancelWorkflowRunPage));
        var normal = page.CancelAsync(item);
        await waiting.Task.WaitAsync(TestContext.CancellationToken);
        item = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());
        var forcePage = Assert.IsInstanceOfType<ForceCancelWorkflowRunPage>(
            item.MoreCommands.OfType<CommandContextItem>().Single(c => c.Command is ForceCancelWorkflowRunPage).Command);
        var confirmation = Assert.IsInstanceOfType<FormContent>(forcePage.GetContent().Single());

        confirmation.SubmitForm("", """{"action":"confirm"}""");
        await forcePage.CurrentSubmission;
        await normal;

        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Cancel_StopsBeforeMutationWhenAccountChangesDuringRefresh()
    {
        var response = new TaskCompletionSource<GitHubWorkflowRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Run()]);
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>())).Returns(response.Task);
        var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        var item = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());

        var cancellation = page.CancelAsync(item);
        auth.SignOut();
        response.SetResult(Run());
        await cancellation;

        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        page.GetItems();
        Assert.AreEqual("Sign in to view workflow runs", page.EmptyContent!.Title);
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
        new(id, "CI", "Fix palette flicker", "mona", "in_progress", null, Now.AddMinutes(-12),
            new Uri($"https://github.com/o/r/actions/runs/{id}"), "push", "main", "abc123", 42, 2, Now);

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
        => await Loaded(client, Auth());

    private static async Task<ActionsPage> Loaded(IActionsClient client, AuthService auth)
    {
        var page = Page(client, auth);
        page.OpenRepository("o/r");
        page.GetItems();
        await page.CurrentLoad;
        return page;
    }

    private static string Text(WorkflowRunDetails details, string key)
    {
        var data = Assert.IsInstanceOfType<IDetailsLink>(details.Metadata.Single(m => m.Key == key).Data);
        return data.Text;
    }

    private static ITag[] Tags(WorkflowRunDetails details, string key)
    {
        var data = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == key).Data);
        return data.Tags;
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

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri? Uri)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri));
            return Task.FromResult(respond(request));
        }
    }
}
