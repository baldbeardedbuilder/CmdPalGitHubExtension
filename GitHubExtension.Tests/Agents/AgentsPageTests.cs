// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Agents;

[TestClass]
public sealed class AgentsPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] NewestFirst = ["newer", "older"];

    [TestMethod]
    public async Task GetItems_ListsAgentsWithScreenshotFields()
    {
        var client = Client([Agent()]);
        using var page = CreatePage(client.Object, out _, out _);
        Assert.AreEqual("Agents", page.Title);
        Assert.AreEqual("Filter agents...", page.PlaceholderText);

        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single());

        Assert.AreEqual("Fix token expiry", item.Title);
        Assert.AreEqual("microsoft/PowerToys \u00b7 claude-sonnet-5 \u00b7 12m ago", item.Subtitle);
        Assert.AreEqual("Working", item.Tags.Single().Text);
        Assert.AreSame(Icons.Agents, item.Icon);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("in_progress", "Working", 0x69, 0x73, 0xFF)]
    [DataRow("queued", "Waiting", 0x8C, 0x95, 0x9F)]
    [DataRow("idle", "Waiting", 0x8C, 0x95, 0x9F)]
    [DataRow("waiting_for_user", "Waiting", 0x8C, 0x95, 0x9F)]
    [DataRow("completed", "Done", 0x2D, 0xA4, 0x4E)]
    [DataRow("failed", "Failed", 0xE5, 0x53, 0x4B)]
    [DataRow("timed_out", "Timed out", 0xE5, 0x53, 0x4B)]
    [DataRow("cancelled", "Cancelled", 0x8C, 0x95, 0x9F)]
    [DataRow("future_state", "future_state", 0x8C, 0x95, 0x9F)]
    public void StateTags_MapApiStatesToTextAndColor(string state, string label, int red, int green, int blue)
    {
        var tag = AgentFormatting.StateTag(state);

        Assert.AreEqual(label, tag.Text);
        Assert.IsTrue(tag.Foreground.HasValue);
        Assert.AreEqual((byte)red, tag.Foreground.Color.R);
        Assert.AreEqual((byte)green, tag.Foreground.Color.G);
        Assert.AreEqual((byte)blue, tag.Foreground.Color.B);
        Assert.AreEqual((byte)0x33, tag.Background.Color.A);
    }

    [TestMethod]
    [DataRow("TOKEN")]
    [DataRow("powertoys")]
    [DataRow("sonnet")]
    [DataRow("working")]
    [DataRow("in_progress")]
    [DataRow("token powertoys sonnet working")]
    public async Task Filter_MatchesTitleRepoModelAndState(string query)
    {
        using var page = CreatePage(Client([Agent(), Agent("other") with { Title = "Other", RepositoryFullName = "o/r", Model = "different", State = "completed" }]).Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        page.SearchText = query;

        Assert.AreEqual("task-1", Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single()).Task.Id);
    }

    [TestMethod]
    public async Task Filter_NoMatchesShowsSearchEmptyStateAndClearingRestoresList()
    {
        using var page = CreatePage(Client([Agent()]).Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        page.SearchText = "missing";

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No agents found", page.EmptyContent!.Title);
        Assert.AreEqual("Nothing matches \"missing\"", page.EmptyContent.Subtitle);
        page.SearchText = "  ";
        Assert.HasCount(1, page.GetItems());
    }

    [TestMethod]
    public async Task EmptyList_ShowsNoAgentsAndSupportsRefresh()
    {
        using var page = CreatePage(Client([]).Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No agents yet", page.EmptyContent!.Title);
        Assert.IsInstanceOfType<RefreshAgentsCommand>(page.EmptyContent.Command);
    }

    [TestMethod]
    public async Task LoadMore_DeduplicatesAndSortsNewestActivityFirst()
    {
        var next = new Uri("https://api.github.com/agents/tasks?page=2");
        var client = Client([Agent("older") with { UpdatedAt = Now.AddHours(-2) }], next);
        client.Setup(c => c.GetTasksAsync(Account, next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTasksPageResult([Agent("older"), Agent("newer")], null));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        Assert.IsTrue(page.HasMoreItems);

        page.LoadMore();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(NewestFirst, page.GetItems().Cast<AgentItem>().Select(i => i.Task.Id).ToArray());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task Refresh_ReplacesTasksAndKeepsSearch()
    {
        var client = new Mock<IAgentsClient>();
        client.SetupSequence(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTasksPageResult([Agent("old")], null))
            .ReturnsAsync(new AgentTasksPageResult([Agent("new") with { State = "completed" }], null));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        page.SearchText = "token";

        await page.RefreshAsync();

        var item = Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single());
        Assert.AreEqual("new", item.Task.Id);
        Assert.AreEqual("Done", item.Tags.Single().Text);
        Assert.AreEqual("token", page.SearchText);
    }

    [TestMethod]
    public async Task OpenAndMoreCommands_UseTaskUrlAndRefresh()
    {
        using var page = CreatePage(Client([Agent()]).Object, out var browser, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single());

        Assert.IsInstanceOfType<OpenInBrowserCommand>(item.Command).Invoke();

        Assert.AreEqual(Agent().WebUrl, browser.LastOpened);
        var more = item.MoreCommands.Cast<CommandContextItem>().ToArray();
        var copy = Assert.IsInstanceOfType<CopyTextCommand>(more[0].Command);
        Assert.AreEqual("Copy URL", copy.Name);
        Assert.IsInstanceOfType<RefreshAgentsCommand>(more[1].Command).Invoke();
        await page.CurrentLoad;
    }

    [TestMethod]
    public async Task LoadFailure_ShowsErrorAndRefreshRetries()
    {
        var client = new Mock<IAgentsClient>();
        client.SetupSequence(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("permission denied"))
            .ReturnsAsync(new AgentTasksPageResult([Agent()], null));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Couldn't load agents", page.EmptyContent!.Title);
        Assert.AreEqual("permission denied", page.EmptyContent.Subtitle);
        Assert.IsFalse(page.IsLoading);

        Assert.IsInstanceOfType<RefreshAgentsCommand>(page.EmptyContent.Command).Invoke();
        await page.CurrentLoad;
        Assert.HasCount(1, page.GetItems());
    }

    [TestMethod]
    public async Task LoadMoreFailure_RemainsVisibleAlongsideExistingTasks()
    {
        var next = new Uri("https://api.github.com/agents/tasks?page=2");
        var client = Client([Agent()], next);
        client.Setup(c => c.GetTasksAsync(Account, next, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        page.LoadMore();
        await page.CurrentLoad;

        var items = page.GetItems();
        Assert.HasCount(2, items);
        Assert.AreEqual("Couldn't load agents", items[0].Title);
        Assert.AreEqual("rate limited", items[0].Subtitle);
        Assert.IsInstanceOfType<RefreshAgentsCommand>(items[0].Command);
        Assert.AreEqual("Fix token expiry", items[1].Title);
    }

    [TestMethod]
    public async Task LoadTimeout_ShowsRetryableError()
    {
        var client = new Mock<IAgentsClient>();
        client.Setup(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("GitHub took too long to respond. Try refreshing agents.", page.EmptyContent!.Subtitle);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task RefreshDuringLoad_DiscardsStaleResponseAndKeepsLoading()
    {
        var first = new TaskCompletionSource<AgentTasksPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<AgentTasksPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IAgentsClient>();
        var calls = 0;
        client.Setup(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    return first.Task;
                }

                return second.Task;
            });
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await started.Task;
        var oldLoad = page.CurrentLoad;
        var newLoad = page.RefreshAsync();
        first.SetResult(new AgentTasksPageResult([Agent("stale")], null));
        await oldLoad;

        Assert.IsTrue(page.IsLoading);
        Assert.IsEmpty(page.GetItems());
        second.SetResult(new AgentTasksPageResult([Agent("fresh")], null));
        await newLoad;
        Assert.AreEqual("fresh", Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single()).Task.Id);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task AccountChange_CancelsLoadAndClearsPreviousUserTasks()
    {
        var pending = new TaskCompletionSource<AgentTasksPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IAgentsClient>();
        client.Setup(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return pending.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        var token = await started.Task;
        var load = page.CurrentLoad;

        auth.SignOut();
        pending.SetResult(new AgentTasksPageResult([Agent()], new Uri("https://api.github.com/agents/tasks?page=2")));
        await load;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsEmpty(page.GetItems());
        Assert.IsFalse(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task Dispose_CancelsLoadAndStopsFurtherRequests()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IAgentsClient>();
        client.Setup(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, Uri? _, CancellationToken token) =>
            {
                started.SetResult(token);
                await System.Threading.Tasks.Task.Delay(Timeout.Infinite, token);
                return new AgentTasksPageResult([], null);
            });
        var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        var token = await started.Task;

        page.Dispose();
        await page.CurrentLoad;
        page.GetItems();
        await page.RefreshAsync();

        Assert.IsTrue(token.IsCancellationRequested);
        client.Verify(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void Subtitle_OmitsAbsentModelAndShowsEnrichmentErrors()
    {
        var task = Agent() with { Model = null, RepositoryFullName = null, DetailsError = "Couldn't load the repository." };

        Assert.AreEqual("Repository unavailable \u00b7 12m ago \u00b7 Couldn't load the repository.", AgentFormatting.Subtitle(task, Now));
    }

    private static GitHubAgentTask Agent(string id = "task-1") =>
        new(id, "Fix token expiry", new Uri($"https://github.com/copilot/tasks/{id}"), "in_progress",
            Now.AddMinutes(-12), 1, "microsoft/PowerToys", "claude-sonnet-5");

    private static Mock<IAgentsClient> Client(GitHubAgentTask[] tasks, Uri? next = null)
    {
        var client = new Mock<IAgentsClient>();
        client.Setup(c => c.GetTasksAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTasksPageResult(tasks, next));
        return client;
    }

    private static AgentsPage CreatePage(IAgentsClient client, out FakeBrowser browser, out AuthService auth)
    {
        auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.GetUtcNow()).Returns(Now);
        return new AgentsPage(auth, client, browser, time.Object);
    }
}
