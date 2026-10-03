// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public sealed class PageLifetimeTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "token");
    private static readonly Uri ApiUrl = new("https://api.github.com/repos/o/r/issues/1");
    private static readonly GitHubIssue Issue = new(1, "Issue", null, SubjectState.Open,
        new Uri("https://github.com/o/r/issues/1"), DateTimeOffset.UtcNow, "octocat", [], [], 0);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IssueDetails_AccountChangeOrDisposalCancelsLateLoad(bool dispose)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        var auth = CreateAuth();
        using var page = new IssueDetailsPage(auth, client.Object, new FakeBrowser(_ => null));
        page.LoadIssue(Account, ApiUrl, "o/r");
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var work = page.CurrentLoad;
        if (dispose)
        {
            page.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        var events = 0;
        page.ItemsChanged += (_, _) => events++;
        response.SetResult(Issue);
        await work;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual(0, events);
        Assert.DoesNotContain("Issue #1", ((FormContent)page.GetContent().Single()).TemplateJson);
        page.HandleSubmit(IssueDetailsActions.Retry);
        client.Verify(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task IssueDetails_ReplacementCancelsOldLoadAndKeepsNewContent()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        var secondUrl = new Uri("https://api.github.com/repos/o/r/issues/2");
        client.Setup(c => c.GetIssueAsync(Account, secondUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issue with { Title = "Fresh issue" });
        using var page = new IssueDetailsPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.LoadIssue(Account, ApiUrl, "o/r");
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = page.CurrentLoad;
        page.LoadIssue(Account, secondUrl, "o/r");
        await page.CurrentLoad;
        response.SetException(new GitHubApiException("stale error"));
        await old;
        Assert.IsTrue(token.IsCancellationRequested);
        var template = ((FormContent)page.GetContent().Single()).TemplateJson;
        Assert.Contains("Fresh issue", template);
        Assert.DoesNotContain("stale error", template);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NotificationSubjects_CancelsRequestsAndQueuedWaits(bool dispose)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SubjectDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new System.Collections.Concurrent.ConcurrentBag<CancellationToken>();
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [.. Enumerable.Range(0, 12).Select(i => Notification(i.ToString(System.Globalization.CultureInfo.InvariantCulture)))], null));
        client.Setup(c => c.GetSubjectAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri _, CancellationToken token) =>
            {
                tokens.Add(token);
                if (tokens.Count == 6)
                {
                    started.TrySetResult();
                }

                return response.Task;
            });
        var auth = CreateAuth();
        using var page = new NotificationsPage(auth, client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = page.CurrentLoad;
        var item = (NotificationItem)page.GetItems()[0];
        if (dispose)
        {
            page.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        var changes = 0;
        item.PropChanged += (_, _) => changes++;
        response.SetResult(new SubjectDetails(SubjectState.Closed, Issue.WebUrl) { Issue = Issue });
        await old;
        Assert.HasCount(6, tokens);
        Assert.IsTrue(tokens.All(token => token.IsCancellationRequested));
        Assert.IsNull(item.Subject);
        Assert.AreEqual(0, changes);
        Assert.IsEmpty(page.GetItems());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NotificationMutation_DisposalCancelsWithoutRollback(bool done)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = NotificationClient();
        Func<GitHubAccount, string, CancellationToken, Task> request = (_, _, token) =>
        {
            started.TrySetResult(token);
            return response.Task;
        };
        client.Setup(c => c.MarkAsReadAsync(Account, "1", It.IsAny<CancellationToken>())).Returns(request);
        client.Setup(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>())).Returns(request);
        using var page = new NotificationsPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();
        if (done)
        {
            page.MarkAsDone(item);
        }
        else
        {
            page.MarkAsRead(item);
        }

        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var work = page.CurrentMutation;
        page.Dispose();
        var changes = 0;
        page.ItemsChanged += (_, _) => changes++;
        item.PropChanged += (_, _) => changes++;
        response.SetException(new GitHubApiException("stale failure"));
        await work;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(0, changes);
        Assert.IsEmpty(page.GetItems());
    }

    [TestMethod]
    public async Task NotificationDetails_RefreshPreservesDestinationAndDisposalCancelsIt()
    {
        var auth = CreateAuth();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var issues = new Mock<IIssuesClient>();
        issues.Setup(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        var browser = new FakeBrowser(_ => null);
        using var template = new IssueDetailsPage(auth, issues.Object, browser);
        using var page = new NotificationsPage(auth, NotificationClient().Object, browser, issueDetails: template);
        page.GetItems();
        await page.CurrentLoad;
        var destination = (IssueDetailsPage)page.GetItems().Single().Command!;
        destination.GetContent();
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.RefreshAsync();
        Assert.AreSame(destination, page.GetItems().Single().Command);
        Assert.IsFalse(token.IsCancellationRequested);
        destination.GetContent();
        await page.CurrentMutation;
        Assert.IsFalse(((NotificationItem)page.GetItems().Single()).Unread);
        Assert.IsFalse(token.IsCancellationRequested);
        page.Dispose();
        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Issue);
        await destination.CurrentLoad;
        Assert.IsFalse(destination.IsLoading);
    }

    [TestMethod]
    public void RepositoryGraphs_WeakReuseDoesNotRetainRetiredChildren()
    {
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var actions = new ActionsPage(auth, Mock.Of<IActionsClient>(), browser);
        using var repos = new ReposPage(auth, Mock.Of<IRepositoriesClient>(), browser, issues, pulls,
            actions: actions, agentsClient: Mock.Of<IAgentsClient>());
        var references = Enumerable.Range(0, 25).Select(i => CreateRetiredGraph(repos, i)).ToArray();
        Collect();
        Assert.IsTrue(references.All(reference => !reference.TryGetTarget(out _)));
        var repository = Repository("o/active");
        var first = repos.CreateRepositoryPage(repository);
        first.SearchText = "keep";
        Assert.AreSame(first, repos.CreateRepositoryPage(repository));
        Assert.AreEqual("keep", first.SearchText);
        GC.KeepAlive(auth);
    }

    [TestMethod]
    public void RepositoryChild_HoldsOwnerForProviderTeardown()
    {
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var repos = new ReposPage(auth, Mock.Of<IRepositoriesClient>(), browser, issues, pulls);
        var child = CreateLiveChild(repos);
        Collect();
        Assert.IsNotNull(child.Owner);
        repos.Dispose();
        Assert.IsEmpty(child.GetItems());
        Assert.IsEmpty(child.Owner.GetItems());
    }

    [TestMethod]
    public async Task MergeDestination_HoldsRepositoryGraphForOwnerTeardown()
    {
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult([new GitHubPullRequest
            {
                Number = 1,
                Title = "Pull request",
                State = SubjectState.Open,
                WebUrl = new Uri("https://github.com/o/r/pull/1"),
            }], null));
        var mergeClient = new Mock<IPullRequestMergeClient>();
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, client.Object, browser, mergeClient: mergeClient.Object);
        using var repos = new ReposPage(auth, Mock.Of<IRepositoriesClient>(), browser, issues, pulls);
        var destination = await CreateLiveMerge(repos);
        Collect();
        Assert.IsNotNull(destination.Owner?.Owner);
        repos.Dispose();
        Assert.Contains("no longer active", ((FormContent)destination.GetContent().Single()).TemplateJson);
        mergeClient.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task RepositorySearches_CreatePagesLazilyAndReleaseRetiredGraphs()
    {
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        var client = new Mock<IRepositoriesClient>();
        client.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult([], null));
        client.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitHubAccount _, string query, CancellationToken _) =>
                new List<GitHubRepository> { Repository($"o/{query}") });
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var actions = new ActionsPage(auth, Mock.Of<IActionsClient>(), browser);
        using var repos = new ReposPage(auth, client.Object, browser, issues, pulls,
            searchDelay: TimeSpan.Zero, actions: actions, agentsClient: Mock.Of<IAgentsClient>());
        repos.GetItems();
        await repos.CurrentLoad;
        var references = new List<WeakReference<RepositoryPage>>();
        for (var i = 0; i < 20; i++)
        {
            repos.SearchText = $"repo{i}";
            await repos.CurrentSearch;
            Assert.HasCount(1, repos.GetItems());
            if (i == 0)
            {
                Assert.AreEqual(0, repos.LiveRepositoryPages);
            }

            references.Add(OpenSearchResult(repos));
        }

        repos.SearchText = string.Empty;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(10);
            Collect();
            if (references.All(reference => !reference.TryGetTarget(out _)))
            {
                break;
            }
        }

        Assert.IsTrue(references.All(reference => !reference.TryGetTarget(out _)),
            $"Retained {references.Count(reference => reference.TryGetTarget(out _))} repository pages.");
        Assert.AreEqual(0, repos.LiveRepositoryPages);
    }

    [TestMethod]
    public async Task RetiredRepositoryRows_CannotCreatePagesAfterAccountChange()
    {
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        var client = new Mock<IRepositoriesClient>();
        client.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult([Repository("o/r")], null));
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var actions = new ActionsPage(auth, Mock.Of<IActionsClient>(), browser);
        using var repos = new ReposPage(auth, client.Object, browser, issues, pulls, actions: actions);
        repos.GetItems();
        await repos.CurrentLoad;
        var row = (RepoItem)repos.GetItems().Single();
        var context = row.MoreCommands.OfType<CommandContextItem>().ElementAt(3);
        Assert.AreEqual(0, repos.LiveRepositoryPages);
        auth.SignOut();
        Assert.IsNull(row.Command);
        Assert.IsNull(context.Command);
        Assert.AreEqual(0, repos.LiveRepositoryPages);
    }

    [TestMethod]
    public async Task NotificationMutations_AreIndependentAndAllCancelOnAccountChange()
    {
        var client = NotificationClient();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([Notification("1"), Notification("2")], null));
        var tokens = new System.Collections.Concurrent.ConcurrentDictionary<string, CancellationToken>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.MarkAsReadAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string id, CancellationToken token) =>
            {
                tokens[id] = token;
                if (tokens.Count == 2)
                {
                    bothStarted.TrySetResult();
                }

                return response.Task;
            });
        var auth = CreateAuth();
        using var page = new NotificationsPage(auth, client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        var items = page.GetItems().Cast<NotificationItem>().ToArray();
        page.MarkAsRead(items[0]);
        var first = page.CurrentMutation;
        page.MarkAsRead(items[1]);
        var second = page.CurrentMutation;
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(tokens.Values.All(token => !token.IsCancellationRequested));
        auth.SignOut();
        Assert.IsTrue(tokens.Values.All(token => token.IsCancellationRequested));
        response.SetException(new GitHubApiException("stale failure"));
        await Task.WhenAll(first, second);
        Assert.IsEmpty(page.GetItems());
        Assert.IsTrue(items.All(item => !item.Unread));
    }

    [TestMethod]
    public async Task NotificationEnrichment_ReentrantDisposalStopsRemainingItemNotifications()
    {
        var client = NotificationClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SubjectDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetSubjectAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.SetResult();
                return response.Task;
            });
        using var page = new NotificationsPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var item = (NotificationItem)page.GetItems().Single();
        var notifications = new List<string>();
        item.PropChanged += (_, args) =>
        {
            notifications.Add(args.PropertyName);
            page.Dispose();
        };
        response.SetResult(new SubjectDetails(SubjectState.Open, Issue.WebUrl, Issue: Issue));
        await page.CurrentLoad;
        Assert.HasCount(1, notifications);
        Assert.AreEqual("Details", notifications[0]);
        Assert.IsEmpty(page.GetItems());
    }

    [TestMethod]
    public async Task SignIn_DisposalCancelsAndDoesNotPersistLateAccount()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), "token", It.IsAny<CancellationToken>()))
            .Returns((GitHubHost _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        var store = new InMemoryAccountStore();
        using var auth = new AuthService(store, client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new SignInPage(auth, () => string.Empty, _ => { });
        page.HandleSubmit(SignInActions.Enterprise, """{"serverUrl":"github.example.com","token":"token"}""");
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var work = page.CurrentSignIn;
        page.Dispose();
        var changes = 0;
        page.ItemsChanged += (_, _) => changes++;
        response.SetResult("mona");
        await work;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsNull(auth.CurrentAccount);
        Assert.IsNull(store.Load());
        Assert.AreEqual(0, changes);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public void Provider_DisposesOwnedHttpOnceAndDetachesPages()
    {
        var auth = CreateAuth();
        using var handler = new TrackingHandler();
        var http = new HttpClient(handler);
        var client = new Mock<INotificationsClient>();
        var injected = client.As<IDisposable>();
        var provider = new GitHubCommandsProvider(auth, () => string.Empty, notificationsClient: client.Object,
            browser: new FakeBrowser(_ => null), httpFactory: () => http);
        var home = (HomePage)provider.GetCommand(HomePage.PageId)!;
        var notifications = (NotificationsPage)provider.GetCommand(NotificationsPage.PageId)!;
        provider.Dispose();
        provider.Dispose();
        var changes = 0;
        home.ItemsChanged += (_, _) => changes++;
        notifications.ItemsChanged += (_, _) => changes++;
        provider.ItemsChanged += (_, _) => changes++;
        auth.SignOut();
        Assert.AreEqual(1, handler.Disposals);
        Assert.AreEqual(0, changes);
        Assert.IsEmpty(home.GetItems());
        notifications.GetItems();
        client.VerifyNoOtherCalls();
        injected.Verify(client => client.Dispose(), Times.Never);
    }

    [TestMethod]
    public void AuthService_DisposesOwnedHttpButNotInjectedClient()
    {
        using var handler = new TrackingHandler();
        var http = new HttpClient(handler);
        var owned = AuthService.CreateWithOwnedHttp(new InMemoryAccountStore(), http,
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        owned.Dispose();
        owned.Dispose();
        Assert.AreEqual(1, handler.Disposals);
        var client = new Mock<IGitHubAuthClient>();
        var disposable = client.As<IDisposable>();
        using var injected = new AuthService(new InMemoryAccountStore(), client.Object,
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        injected.Dispose();
        disposable.Verify(client => client.Dispose(), Times.Never);
    }

    private static AuthService CreateAuth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static GitHubNotification Notification(string id) =>
        new(id, "Issue", "Issue", ApiUrl, "o/r", new Uri("https://github.com/o/r"), "mention", true, DateTimeOffset.UtcNow);

    private static Mock<INotificationsClient> NotificationClient()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([Notification("1")], null));
        client.Setup(c => c.GetSubjectAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectDetails(SubjectState.Open, Issue.WebUrl) { Issue = Issue });
        return client;
    }

    private static GitHubRepository Repository(string name) => Repositories.RepoFormattingTests.Repo(name);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<RepositoryPage> CreateRetiredGraph(ReposPage repos, int i)
    {
        var page = repos.CreateRepositoryPage(Repository($"o/repo{i}"));
        page.GetItems();
        return new(page);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RepositoryIssuesPage CreateLiveChild(ReposPage repos) =>
        (RepositoryIssuesPage)repos.CreateRepositoryPage(Repository("o/r")).GetItems().Single(i => i.Title == "Issues").Command!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<MergePullRequestPage> CreateLiveMerge(ReposPage repos)
    {
        var page = (RepositoryPullRequestsPage)repos.CreateRepositoryPage(Repository("o/r")).GetItems()
            .Single(item => item.Title == "Pull Requests").Command!;
        page.GetItems();
        await page.CurrentLoad;
        return page.GetItems().Single().MoreCommands.OfType<CommandContextItem>()
            .Select(context => context.Command).OfType<MergePullRequestPage>().Single();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<RepositoryPage> OpenSearchResult(ReposPage repos)
    {
        var page = (RepositoryPage)repos.GetItems().Single().Command!;
        page.GetItems();
        return new(page);
    }

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        internal int Disposals { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Tests must not send HTTP requests.");

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposals++;
            }

            base.Dispose(disposing);
        }
    }
}
