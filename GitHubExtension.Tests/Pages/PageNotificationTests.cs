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
    [DataRow("agents")]
    [DataRow("codespaces")]
    [DataRow("actions")]
    [DataRow("repos")]
    [DataRow("notifications")]
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
                if (propertyName != "EmptyContent")
                {
                    page.GetItems();
                }
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

        page.Open(Account, new Uri("https://api.github.com/repos/o/r/issues/1"), "o/r");
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

    private static (DynamicListPage Page, Func<Task> CurrentLoad, Func<Task> Refresh) CreateListPage(string name, AuthService auth)
    {
        var browser = new FakeBrowser(_ => null);
        switch (name)
        {
            case "agents":
                var agents = new Mock<IAgentsClient>();
                agents.Setup(c => c.GetTasksAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AgentTasksPageResult([new GitHubAgentTask("1", "Test agent", WebUrl, "completed", Now, null)], NextPage));
                var agentsPage = new AgentsPage(auth, agents.Object, browser);
                return (agentsPage, () => agentsPage.CurrentLoad, agentsPage.RefreshAsync);
            case "codespaces":
                var codespaces = new Mock<ICodespacesClient>();
                codespaces.Setup(c => c.GetCodespacesAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new CodespacesPageResult([new GitHubCodespace("test", "Test codespace", "o/r", "main", "Available", Now, WebUrl)], NextPage));
                var codespacesPage = new CodespacesPage(auth, codespaces.Object, browser);
                return (codespacesPage, () => codespacesPage.CurrentLoad, codespacesPage.RefreshAsync);
            case "actions":
                var actions = new Mock<IActionsClient>();
                actions.Setup(c => c.GetRunsAsync(Account, "o/r", It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new WorkflowRunsPageResult([new GitHubWorkflowRun(1, "Build", "Test build", "octocat", "completed", "success", Now, WebUrl)], NextPage));
                var actionsPage = new ActionsPage(auth, actions.Object, browser);
                actionsPage.OpenRepository("o/r");
                return (actionsPage, () => actionsPage.CurrentLoad, actionsPage.RefreshAsync);
            case "repos":
                var repos = new Mock<IRepositoriesClient>();
                repos.Setup(c => c.GetMyRepositoriesAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new RepositoriesPageResult([new GitHubRepository("o/r", WebUrl, "Test repo", false, false, false, "C#", 0, 0, Now, null)], NextPage));
                repos.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync([]);
                var issuesPage = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
                var pullRequestsPage = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
                var reposPage = new ReposPage(auth, repos.Object, browser, issuesPage, pullRequestsPage, searchDelay: TimeSpan.Zero);
                return (reposPage, () => reposPage.CurrentLoad, reposPage.RefreshAsync);
            case "notifications":
                var notifications = new Mock<INotificationsClient>();
                notifications.Setup(c => c.GetNotificationsAsync(Account, It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new NotificationsPageResult([new GitHubNotification("1", "Test notification", "Discussion", null, "o/r", WebUrl, "mention", true, Now)], NextPage));
                var notificationsPage = new NotificationsPage(auth, notifications.Object, browser);
                return (notificationsPage, () => notificationsPage.CurrentLoad, notificationsPage.RefreshAsync);
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }
    }
}
