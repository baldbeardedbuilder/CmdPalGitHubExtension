// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public sealed class PageNotificationTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly Uri WebUrl = new("https://github.com/o/r");
    private static readonly Uri NextPage = new("https://api.github.com/items?page=2");
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DoNotParallelize]
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task PageDiagnostics_LoadFailureAndRecoveryShareCallerCorrelation(string name)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue, verboseReads: true);
        using var caller = OperationDiagnostics.Begin(DiagnosticEvent.Mutation);
        var fail = true;
        var (page, load, refresh) = CreateListPage(name, CreateAuth(), () => fail);
        try
        {
            page.GetItems();
            await load();
            fail = false;
            await refresh();
            var outcomes = entries.Where(e => e.Event == DiagnosticEvent.PageLoad).ToArray();
            Assert.HasCount(1, outcomes.Where(e => e.Outcome == DiagnosticOutcome.Failed));
            Assert.HasCount(1, outcomes.Where(e => e.Outcome == DiagnosticOutcome.Completed));
            Assert.IsTrue(outcomes.All(e => e.OperationId == caller.Id));
            Assert.IsFalse(entries.Any(e => e.ToString().Contains("rate limited", StringComparison.Ordinal)));
        }
        finally
        {
            (page as IDisposable)?.Dispose();
            caller.Complete();
        }
    }

    [TestMethod]
    public async Task ErrorNotifications_CanReturnHomeAndOpenAnotherPage()
    {
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        var repos = new Mock<IRepositoriesClient>();
        repos.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        var notifications = new Mock<INotificationsClient>();
        notifications.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [new GitHubNotification("1", "Test notification", "Discussion", null, "o/r", WebUrl, "mention", true, Now)], null));
        using var provider = new GitHubCommandsProvider(auth, () => string.Empty, notifications.Object, browser, repos.Object);
        var home = Assert.IsInstanceOfType<HomePage>(provider.TopLevelCommands().Single().Command);
        var reposPage = Assert.IsInstanceOfType<ReposPage>(home.GetItems().Single(item => item.Title == "Repos").Command);
        reposPage.GetItems();
        await reposPage.CurrentLoad;
        Assert.IsEmpty(reposPage.GetItems());
        Assert.AreEqual("rate limited", reposPage.EmptyContent!.Subtitle);

        Assert.AreSame(home, provider.GetCommand(HomePage.PageId));
        var notificationsPage = Assert.IsInstanceOfType<NotificationsPage>(
            home.GetItems().Single(item => item.Title == "Notifications").Command);
        Assert.AreSame(notificationsPage, provider.GetCommand(NotificationsPage.PageId));
        notificationsPage.GetItems();
        await notificationsPage.CurrentLoad;
        Assert.AreEqual("Test notification", notificationsPage.GetItems().Single().Title);
        Assert.IsFalse(notificationsPage.IsLoading);
        Assert.AreSame(reposPage, provider.GetCommand(ReposPage.PageId));
        Assert.AreEqual("rate limited", reposPage.EmptyContent.Subtitle);
    }

    [TestMethod]
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task ErrorNotifications_ReentrantReadSettlesAndRefreshRecovers(string name)
    {
        var fail = true;
        var (page, currentLoad, refresh) = CreateListPage(name, CreateAuth(), () => fail);
        var emptyEvents = 0;
        page.PropChanged += (_, args) =>
        {
            if (args.PropertyName == "EmptyContent" && Interlocked.Increment(ref emptyEvents) <= 10)
            {
                page.GetItems();
            }
        };

        try
        {
            page.GetItems();
            await currentLoad();
            Assert.IsEmpty(page.GetItems());
            Assert.AreEqual("rate limited", page.EmptyContent!.Subtitle);
            Assert.IsFalse(page.IsLoading);
            Assert.IsLessThan(10, emptyEvents, "Reading an error must not keep publishing EmptyContent.");

            var error = page.EmptyContent;
            var events = emptyEvents;
            page.GetItems();
            Assert.AreSame(error, page.EmptyContent);
            Assert.AreEqual(events, emptyEvents);

            fail = false;
            await refresh();
            Assert.HasCount(1, page.GetItems());
            Assert.IsFalse(page.IsLoading);
            Assert.AreNotEqual("rate limited", page.EmptyContent!.Subtitle);
            Assert.IsLessThan(10, emptyEvents);
        }
        finally
        {
            (page as IDisposable)?.Dispose();
        }
    }

    [TestMethod]
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task ListNotifications_HostCanReadDuringLoadRefreshAndAccountChange(string name)
    {
        var auth = CreateAuth();
        var (page, currentLoad, refresh) = CreateListPage(name, auth);
        var blocked = new ConcurrentQueue<string>();
        var properties = new ConcurrentQueue<string>();
        var itemEvents = 0;
        page.PropChanged += (_, args) =>
        {
            var propertyName = args.PropertyName;
            properties.Enqueue(propertyName);
            CheckRead(() =>
            {
                _ = currentLoad();
                page.GetItems();
            }, propertyName, blocked);
        };
        page.ItemsChanged += (_, _) =>
        {
            Interlocked.Increment(ref itemEvents);
            CheckRead(() => page.GetItems(), "ItemsChanged", blocked);
        };

        try
        {
            page.GetItems();
            await currentLoad();
            Assert.HasCount(1, page.GetItems());
            Assert.IsFalse(page.IsLoading);
            Assert.IsTrue(page.HasMoreItems);

            page.LoadMore();
            await currentLoad();
            Assert.HasCount(1, page.GetItems());

            await refresh();
            Assert.HasCount(1, page.GetItems());
            Assert.IsFalse(page.IsLoading);

            page.SearchText = "missing";
            if (page is ReposPage repos)
            {
                await repos.CurrentSearch;
            }

            Assert.IsEmpty(page.GetItems());
            page.SearchText = string.Empty;
            Assert.HasCount(1, page.GetItems());

            auth.SignOut();
            Assert.IsEmpty(page.GetItems());
            Assert.IsFalse(page.IsLoading);
            Assert.IsFalse(page.HasMoreItems);
            Assert.Contains("IsLoading", properties);
            Assert.Contains("HasMoreItems", properties);
            Assert.Contains("EmptyContent", properties);
            Assert.IsGreaterThan(0, itemEvents);
            Assert.IsEmpty(blocked, string.Join(", ", blocked));
        }
        finally
        {
            (page as IDisposable)?.Dispose();
        }
    }

    [TestMethod]
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task ListLifecycle_RefreshCancelsOldRequestAndIgnoresLateFailure(string name)
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (page, currentLoad, refresh) = CreateListPage(name, CreateAuth(), beforeLoad: async (_, token) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult(token);
                await first.Task;
                throw new GitHubApiException("stale error");
            }

            await second.Task;
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        var oldToken = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldLoad = currentLoad();
        var newLoad = refresh();
        first.SetResult();
        await oldLoad;

        Assert.IsTrue(oldToken.IsCancellationRequested);
        Assert.IsTrue(page.IsLoading);
        Assert.IsEmpty(page.GetItems());
        Assert.AreNotEqual("stale error", page.EmptyContent!.Subtitle);
        second.SetResult();
        await newLoad;
        Assert.HasCount(1, page.GetItems());
        Assert.IsFalse(page.IsLoading);
        Assert.IsTrue(page.HasMoreItems);
    }

    [TestMethod]
    [DataRow("agents", false)]
    [DataRow("codespaces", false)]
    [DataRow("actions", false)]
    [DataRow("repos", false)]
    [DataRow("notifications", false)]
    [DataRow("issues", false)]
    [DataRow("pull-requests", false)]
    [DataRow("agents", true)]
    [DataRow("codespaces", true)]
    [DataRow("actions", true)]
    [DataRow("repos", true)]
    [DataRow("notifications", true)]
    [DataRow("issues", true)]
    [DataRow("pull-requests", true)]
    public async Task ListLifecycle_AccountChangeOrDisposalCancelsAndRejectsLateResponse(string name, bool dispose)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = CreateAuth();
        var calls = 0;
        var (page, currentLoad, refresh) = CreateListPage(name, auth, beforeLoad: (_, token) =>
        {
            Interlocked.Increment(ref calls);
            started.SetResult(token);
            return pending.Task;
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var load = currentLoad();
        if (dispose)
        {
            lifetime.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        var events = 0;
        page.ItemsChanged += (_, _) => events++;
        pending.SetResult();
        await load;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsEmpty(page.GetItems());
        Assert.IsFalse(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
        page.LoadMore();
        await refresh();
        Assert.AreEqual(1, calls);
        Assert.AreEqual(0, events);
    }

    [TestMethod]
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task ListLifecycle_PaginationFailurePreservesItemsAndRefreshRecovers(string name)
    {
        var fail = false;
        var calls = 0;
        var (page, currentLoad, refresh) = CreateListPage(name, CreateAuth(), () => fail, (_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        await currentLoad();
        var item = page.GetItems().Single();
        fail = true;
        page.LoadMore();
        await currentLoad();
        var items = page.GetItems();
        Assert.Contains(item, items);
        Assert.AreEqual("rate limited", page.EmptyContent!.Subtitle);
        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual(2, calls);
        fail = false;
        await refresh();
        Assert.HasCount(1, page.GetItems());
        Assert.AreNotEqual("rate limited", page.EmptyContent!.Subtitle);
    }

    [TestMethod]
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task ListLifecycle_TimeoutSettlesAndRefreshRetries(string name)
    {
        var timeout = true;
        var (page, currentLoad, refresh) = CreateListPage(name, CreateAuth(), beforeLoad: (_, _) =>
            timeout ? Task.FromException(new TaskCanceledException()) : Task.CompletedTask);
        using var lifetime = (IDisposable)page;
        page.GetItems();
        await currentLoad();

        Assert.IsEmpty(page.GetItems());
        Assert.Contains("took too long", page.EmptyContent!.Subtitle);
        Assert.IsFalse(page.IsLoading);
        var empty = page.EmptyContent;
        page.GetItems();
        Assert.AreSame(empty, page.EmptyContent);
        timeout = false;
        await refresh();
        Assert.HasCount(1, page.GetItems());
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task IssueNotifications_HostCanReadContentWhenLoadingCompletes()
    {
        var auth = CreateAuth();
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssueAsync(Account, It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubIssue(1, "Test issue", "Body", SubjectState.Open, WebUrl, Now, "octocat", [], [], 0));
        var page = new IssueDetailsPage(auth, client.Object, new FakeBrowser(_ => null));
        var blocked = new ConcurrentQueue<string>();
        var events = 0;
        page.PropChanged += (_, args) => CheckRead(() => page.GetContent(), args.PropertyName, blocked);
        page.ItemsChanged += (_, _) =>
        {
            Interlocked.Increment(ref events);
            CheckRead(() => page.GetContent(), "ItemsChanged", blocked);
        };

        page.LoadIssue(Account, new Uri("https://api.github.com/repos/o/r/issues/1"), "o/r");
        await page.CurrentLoad;

        Assert.IsFalse(page.IsLoading);
        Assert.Contains("Test issue", Assert.IsInstanceOfType<FormContent>(page.GetContent().Single()).TemplateJson);
        auth.SignOut();
        Assert.IsGreaterThan(0, events);
        Assert.IsEmpty(blocked, string.Join(", ", blocked));
    }

    [TestMethod]
    public async Task SubjectNotifications_HostCanReadListDuringEnrichment()
    {
        var subject = new TaskCompletionSource<SubjectDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<INotificationsClient>();
        var apiUrl = new Uri("https://api.github.com/repos/o/r/issues/1");
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [new GitHubNotification("1", "Test issue", "Issue", apiUrl, "o/r", WebUrl, "mention", true, Now)], null));
        client.Setup(c => c.GetSubjectAsync(Account, apiUrl, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.SetResult();
                return subject.Task;
            });
        var page = new NotificationsPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await started.Task;
        var item = Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single());
        var blocked = new ConcurrentQueue<string>();
        var events = 0;
        item.PropChanged += (_, args) =>
        {
            Interlocked.Increment(ref events);
            CheckRead(() => page.GetItems(), args.PropertyName, blocked);
        };

        subject.SetResult(new SubjectDetails(SubjectState.Open, WebUrl));
        await page.CurrentLoad;

        Assert.AreEqual(SubjectState.Open, item.Subject!.State);
        Assert.AreEqual("Open", item.Tags.Single().Text);
        Assert.IsGreaterThan(0, events);
        Assert.IsEmpty(blocked, string.Join(", ", blocked));
    }

    private static void CheckRead(Action read, string name, ConcurrentQueue<string> blocked)
    {
        var readTask = Task.Factory.StartNew(read, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (!readTask.Wait(TimeSpan.FromSeconds(2)))
        {
            blocked.Enqueue(name);
        }
    }

    private static AuthService CreateAuth() =>
        new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static (DynamicListPage Page, Func<Task> CurrentLoad, Func<Task> Refresh) CreateListPage(
        string name, AuthService auth, Func<bool>? fail = null, Func<Uri?, CancellationToken, Task>? beforeLoad = null)
    {
        var browser = new FakeBrowser(_ => null);
        switch (name)
        {
            case "agents":
                var agents = new Mock<IAgentsClient>();
                agents.Setup(c => c.GetTasksAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, Uri? next, CancellationToken token) =>
                        LoadResult(new AgentTasksPageResult([new GitHubAgentTask("1", "Test agent", WebUrl, "completed", Now, null)], NextPage), fail, next, beforeLoad, token));
                var agentsPage = new AgentsPage(auth, agents.Object, browser);
                return (agentsPage, () => agentsPage.CurrentLoad, agentsPage.RefreshAsync);
            case "codespaces":
                var codespaces = new Mock<ICodespacesClient>();
                codespaces.Setup(c => c.GetCodespacesAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, Uri? next, CancellationToken token) =>
                        LoadResult(new CodespacesPageResult([new GitHubCodespace("test", "Test codespace", "o/r", "main", "Available", Now, WebUrl)], NextPage), fail, next, beforeLoad, token));
                var codespacesPage = new CodespacesPage(auth, codespaces.Object, browser);
                return (codespacesPage, () => codespacesPage.CurrentLoad, codespacesPage.RefreshAsync);
            case "actions":
                var actions = new Mock<IActionsClient>();
                actions.Setup(c => c.GetRunsAsync(Account, "o/r", It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, string _, Uri? next, CancellationToken token) =>
                        LoadResult(new WorkflowRunsPageResult([new GitHubWorkflowRun(1, "Build", "Test build", "octocat", "in_progress", null, Now, WebUrl)], NextPage), fail, next, beforeLoad, token));
                var actionsPage = new ActionsPage(auth, actions.Object, browser);
                actionsPage.OpenRepository("o/r");
                return (actionsPage, () => actionsPage.CurrentLoad, actionsPage.RefreshAsync);
            case "repos":
                var repos = new Mock<IRepositoriesClient>();
                repos.Setup(c => c.GetMyRepositoriesAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, Uri? next, CancellationToken token) =>
                        LoadResult(new RepositoriesPageResult([new GitHubRepository("o/r", WebUrl, "Test repo", false, false, false, "C#", 0, 0, Now, null)], NextPage), fail, next, beforeLoad, token));
                repos.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([]);
                var issuesPage = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
                var pullRequestsPage = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
                var reposPage = new ReposPage(auth, repos.Object, browser, issuesPage, pullRequestsPage, searchDelay: TimeSpan.Zero);
                return (reposPage, () => reposPage.CurrentLoad, reposPage.RefreshAsync);
            case "notifications":
                var notifications = new Mock<INotificationsClient>();
                notifications.Setup(c => c.GetNotificationsAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, Uri? next, CancellationToken token) =>
                        LoadResult(new NotificationsPageResult([new GitHubNotification("1", "Test notification", "Discussion", null, "o/r", WebUrl, "mention", true, Now)], NextPage), fail, next, beforeLoad, token));
                var notificationsPage = new NotificationsPage(auth, notifications.Object, browser);
                return (notificationsPage, () => notificationsPage.CurrentLoad, notificationsPage.RefreshAsync);
            case "issues":
                var issues = new Mock<IIssuesClient>();
                issues.Setup(c => c.GetIssuesAsync(Account, "o/r", It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, string _, Uri? next, CancellationToken token) =>
                        LoadResult(new IssuesPageResult(
                            [new GitHubIssue(1, "Test issue", "Body", SubjectState.Open, WebUrl, Now, "octocat", [], [], 0)], NextPage), fail, next, beforeLoad, token));
                var repositoryIssues = new RepositoryIssuesPage(auth, issues.Object, browser);
                repositoryIssues.Open("o/r");
                return (repositoryIssues, () => repositoryIssues.CurrentLoad, repositoryIssues.RefreshAsync);
            case "pull-requests":
                var pulls = new Mock<IPullRequestsClient>();
                pulls.Setup(c => c.GetPullRequestsAsync(Account, "o/r", It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .Returns((GitHubAccount _, string _, Uri? next, CancellationToken token) =>
                        LoadResult(new PullRequestsPageResult(
                            [new GitHubPullRequest { Number = 1, Title = "Test pull request", State = SubjectState.Open, WebUrl = WebUrl, CreatedAt = Now, Author = "octocat" }], NextPage), fail, next, beforeLoad, token));
                var repositoryPulls = new RepositoryPullRequestsPage(auth, pulls.Object, browser);
                repositoryPulls.Open("o/r");
                return (repositoryPulls, () => repositoryPulls.CurrentLoad, repositoryPulls.RefreshAsync);
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    private static async Task<T> LoadResult<T>(
        T result, Func<bool>? fail, Uri? next = null,
        Func<Uri?, CancellationToken, Task>? beforeLoad = null, CancellationToken token = default)
    {
        if (beforeLoad is not null)
        {
            await beforeLoad(next, token);
        }

        if (fail?.Invoke() == true)
        {
            throw new GitHubApiException("rate limited");
        }

        return result;
    }
}
