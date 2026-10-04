using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Commands;

[TestClass]
public sealed class PinningTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    public TestContext TestContext { get; set; } = null!;

    public static IEnumerable<string> Destinations => Enum.GetNames<PinDestinationKind>();
    public static IEnumerable<string> RepositoryDestinations => Destinations.Where(name => name.StartsWith("Repository", StringComparison.Ordinal));

    [TestMethod]
    [DynamicData(nameof(Destinations))]
    public async Task Resolve_AllDestinationsSurviveFreshProviderAndUseDefaults(string name)
    {
        var destination = Destination(name);
        string id;
        using (var original = new Fixture(Account))
        {
            var item = original.Provider.GetCommandItem(destination.Id);
            Assert.IsNotNull(item);
            id = item.Command!.Id;
        }
        using var restored = new Fixture(Account);
        var resolved = restored.Provider.GetCommandItem(id);
        Assert.IsNotNull(resolved);
        Assert.AreSame(resolved, restored.Provider.GetCommandItem(id));
        Assert.AreSame(resolved.Command, restored.Provider.GetCommand(id));
        Assert.AreEqual(destination.Title, resolved.Title);
        Assert.AreEqual(id, resolved.Command!.Id);
        if (destination.Kind == PinDestinationKind.Home)
        {
            Assert.IsInstanceOfType<HomePage>(resolved.Command);
            return;
        }
        var page = Assert.IsInstanceOfType<PinnedDestinationPage>(resolved.Command);
        page.GetItems();
        Assert.IsNotNull(page.LoadedPage);
        await Load(page.LoadedPage);
        page.GetItems();

        Assert.AreEqual("", page.SearchText);
        Assert.IsFalse(page.IsLoading);
        switch (destination.Kind)
        {
            case PinDestinationKind.RepositoryIssues:
                Assert.AreEqual(IssueFilters.Open, page.Filters!.CurrentFilterId);
                restored.Issues.Verify(client => client.GetIssuesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
                break;
            case PinDestinationKind.RepositoryPullRequests:
                Assert.AreEqual(PullRequestFilters.Open, page.Filters!.CurrentFilterId);
                restored.Pulls.Verify(client => client.GetPullRequestsAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
                break;
            case PinDestinationKind.RepositoryActions:
                Assert.AreEqual(ActionFilters.Running, page.Filters!.CurrentFilterId);
                restored.Actions.Verify(client => client.GetRunsAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
                break;
            case PinDestinationKind.RepositoryAgents:
                restored.Browsing.Verify(client => client.GetTasksAsync(Account,
                    It.Is<AgentQuery>(query => query.Repository == "octocat/hello" && !query.Archived && query.State == null),
                    null, It.IsAny<CancellationToken>()), Times.Once);
                break;
        }
    }

    [TestMethod]
    [DynamicData(nameof(Destinations))]
    public void DockResolver_ProducesOnePageLaunchButtonWithoutLoadingData(string name)
    {
        using var fixture = new Fixture(Account);
        var destination = Destination(name);
        var item = fixture.Provider.GetCommandItem(destination.Id)!;
        var attributes = Assert.IsInstanceOfType<IExtendedAttributesProvider>(item).GetProperties();
        var dockId = Assert.IsInstanceOfType<string>(attributes[PinItemProperties.DockCommandId]);
        Assert.AreEqual(PinDestination.DockPrefix + destination.Id, dockId);

        var dock = fixture.Provider.GetCommandItem(dockId)!;
        var band = Assert.IsInstanceOfType<IListPage>(dock.Command);
        var button = Assert.ContainsSingle(band.GetItems());

        Assert.AreEqual(dockId, band.Id);
        Assert.AreEqual(item.Command!.Id, button.Command!.Id);
        if (destination.Kind != PinDestinationKind.Home) { Assert.AreSame(item.Command, button.Command); }
        Assert.AreEqual(destination.Kind != PinDestinationKind.Home, button.Command is IDynamicListPage);
        Assert.AreEqual(destination.Title, button.Title);
        Assert.IsNull(Assert.IsInstanceOfType<PinnedDestinationPage>(button.Command).LoadedPage);
        Assert.IsNull(fixture.Provider.GetDockBands());
        Assert.AreSame(dock, fixture.Provider.GetCommandItem(dockId));
        fixture.VerifyNoReads();

        var pointer = WinRT.MarshalInterface<ICommand>.FromManaged(button.Command);
        try
        {
            var interfaceId = typeof(IDynamicListPage).GUID;
            var result = Marshal.QueryInterface(pointer, in interfaceId, out var dynamicPagePointer);
            try
            {
                Assert.AreEqual(destination.Kind == PinDestinationKind.Home ? unchecked((int)0x80004002) : 0, result);
            }
            finally
            {
                if (dynamicPagePointer != IntPtr.Zero) { Marshal.Release(dynamicPagePointer); }
            }
        }
        finally { Marshal.Release(pointer); }
    }

    [TestMethod]
    [DynamicData(nameof(RepositoryDestinations))]
    public async Task RepositoryPins_RejectOtherAccountsAndHostsThenRecoverAfterReauthentication(string name)
    {
        using var fixture = new Fixture(Account);
        var destination = Destination(name);
        var item = fixture.Provider.GetCommandItem(destination.Id)!;
        var page = Assert.IsInstanceOfType<PinnedDestinationPage>(item.Command);
        foreach (var (host, login) in new[] { ("github.com", "mona"), ("github.example.com", "octocat") })
        {
            await fixture.Auth.SignInWithTokenAsync(host, login, TestContext.CancellationToken);
            var signIn = Assert.ContainsSingle(page.GetItems());
            Assert.IsInstanceOfType<SignInPage>(signIn.Command);
            Assert.Contains("@octocat", signIn.Title);
            Assert.Contains("github.com", signIn.Title);
            Assert.IsNull(page.LoadedPage);
            Assert.AreEqual(destination.Id, page.Id);
            page.LoadMore();
            fixture.VerifyNoReads();
        }

        await fixture.Auth.SignInWithTokenAsync("github.com", "OCTOCAT", TestContext.CancellationToken);
        page.GetItems();
        Assert.IsNotNull(page.LoadedPage);
        await Load(page.LoadedPage);
        Assert.IsNotInstanceOfType<SignInPage>(page.GetItems().FirstOrDefault()?.Command);
    }

    [TestMethod]
    public async Task GeneralPins_FollowCurrentAccountAndOfferSignInWhenSignedOut()
    {
        using var fixture = new Fixture(Account);
        var id = PinDestination.GlobalId(PinDestinationKind.Notifications);
        var page = Assert.IsInstanceOfType<PinnedDestinationPage>(fixture.Provider.GetCommandItem(id)!.Command);
        page.GetItems();
        await Load(page.LoadedPage!);
        fixture.Auth.SignOut();

        var signIn = Assert.ContainsSingle(page.GetItems());
        Assert.IsInstanceOfType<SignInPage>(signIn.Command);
        Assert.AreEqual(id, page.Id);
        Assert.IsNull(page.LoadedPage);

        await fixture.Auth.SignInWithTokenAsync("github.example.com", "mona", TestContext.CancellationToken);
        page.GetItems();
        await Load(page.LoadedPage!);
        fixture.Notifications.Verify(client => client.GetNotificationsAsync(
            It.Is<GitHubAccount>(account => account.Login == "mona" && account.Host.Name == "github.example.com"),
            null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AccountChange_CancelsPendingReadRejectsLateDataAndBlocksRetainedPage()
    {
        using var fixture = new Fixture(Account);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<IssuesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Issues.Setup(client => client.GetIssuesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, Uri? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return finish.Task;
            });
        var destination = Destination(nameof(PinDestinationKind.RepositoryIssues));
        var page = Assert.IsInstanceOfType<PinnedDestinationPage>(fixture.Provider.GetCommandItem(destination.Id)!.Command);
        page.GetItems();
        var inner = Assert.IsInstanceOfType<RepositoryIssuesPage>(page.LoadedPage);
        var load = inner.CurrentLoad;
        var cancellation = await started.Task.WaitAsync(TestContext.CancellationToken);

        await fixture.Auth.SignInWithTokenAsync("github.com", "mona", TestContext.CancellationToken);
        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsNull(page.LoadedPage);
        Assert.IsEmpty(inner.GetItems());
        Assert.IsInstanceOfType<SignInPage>(Assert.ContainsSingle(page.GetItems()).Command);
        finish.SetResult(new IssuesPageResult([], null));
        await load.WaitAsync(TestContext.CancellationToken);
        Assert.IsFalse(page.IsLoading);
        fixture.Issues.Verify(client => client.GetIssuesAsync(
            It.Is<GitHubAccount>(account => account.Login == "mona"), It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task NavigationReset_UsesDefaultsWithoutChangingExistingBrowsingPage()
    {
        using var fixture = new Fixture(Account);
        var browsing = Assert.IsInstanceOfType<RepositoryIssuesPage>(fixture.Provider.GetCommand(RepositoryIssuesPage.PageId))
            .ForRepository("octocat/hello");
        using (browsing)
        {
            browsing.Filters!.CurrentFilterId = IssueFilters.Closed;
            browsing.SearchText = "existing search";
            var page = Assert.IsInstanceOfType<PinnedDestinationPage>(
                fixture.Provider.GetCommandItem(Destination(nameof(PinDestinationKind.RepositoryIssues)).Id)!.Command);
            page.GetItems();
            await Load(page.LoadedPage!);
            page.GetItems();
            page.Filters!.CurrentFilterId = IssueFilters.Closed;
            page.SearchText = "temporary search";

            page.SearchText = "";
            page.GetItems();
            await Load(page.LoadedPage!);
            page.GetItems();

            Assert.AreEqual(IssueFilters.Open, page.Filters!.CurrentFilterId);
            Assert.AreEqual("", page.LoadedPage!.SearchText);
            Assert.AreEqual(IssueFilters.Closed, browsing.Filters.CurrentFilterId);
            Assert.AreEqual("existing search", browsing.SearchText);
        }
    }

    [TestMethod]
    public async Task RepositoryAgents_KeepScopeAfterReauthentication()
    {
        using var fixture = new Fixture(Account);
        using var page = new AgentsPage(fixture.Auth, fixture.Agents.Object, new FakeBrowser(_ => null), query: new(Repository: "octocat/hello"));
        fixture.Auth.SignOut();
        await fixture.Auth.SignInWithTokenAsync("github.com", "octocat", TestContext.CancellationToken);
        page.GetItems();
        await page.CurrentLoad;
        fixture.Browsing.Verify(client => client.GetTasksAsync(It.IsAny<GitHubAccount>(),
            It.Is<AgentQuery>(query => query.Repository == "octocat/hello"), null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void HomeSurfaces_ExposeOnlyAgreedDestinationsAndStableGitHubIdentity()
    {
        using var fixture = new Fixture(Account);
        var top = fixture.Provider.TopLevelCommands().Single();
        var homeId = top.Command!.Id;
        var home = Assert.IsInstanceOfType<HomePage>(top.Command);
        foreach (var row in home.GetItems())
        {
            var eligible = row.Title is "Notifications" or "Repos" or "Agents" or "Codespaces";
            Assert.AreEqual(eligible, PinDestination.TryParse(row.Command!.Id, out _), row.Title);
            if (eligible)
            {
                Assert.IsNotNull(fixture.Provider.GetCommandItem(row.Command.Id));
                Assert.IsNotNull(Assert.IsInstanceOfType<IExtendedAttributesProvider>(row).GetProperties()[PinItemProperties.DockCommandId]);
            }
            else { Assert.IsEmpty(row.Command.Id); }
        }
        fixture.Auth.SignOut();
        Assert.AreEqual(homeId, top.Command!.Id);
        Assert.IsInstanceOfType<SignInPage>(top.Command);
        Assert.AreEqual(PinDestination.GlobalId(PinDestinationKind.Home), homeId);
    }

    [TestMethod]
    public void RepositorySurfaces_ExposeScopedDestinationsAndExcludeTransientActions()
    {
        using var fixture = new Fixture(Account);
        using var repositoryAgents = new AgentsPage(fixture.Auth, fixture.Agents.Object,
            new FakeBrowser(_ => null), query: new(Repository: "octocat/hello"));
        using var repository = new RepositoryPage(new FakeBrowser(_ => null),
            Assert.IsInstanceOfType<ActionsPage>(fixture.Provider.GetCommand(ActionsPage.PageId)),
            Tests.Repositories.RepoFormattingTests.Repo("octocat/hello"),
            Assert.IsInstanceOfType<RepositoryIssuesPage>(fixture.Provider.GetCommand(RepositoryIssuesPage.PageId)),
            Assert.IsInstanceOfType<RepositoryPullRequestsPage>(fixture.Provider.GetCommand(RepositoryPullRequestsPage.PageId)),
            fixture.Auth, fixture.Agents.Object, repositoryAgents: repositoryAgents);
        foreach (var row in repository.GetItems())
        {
            var eligible = row.Title is "Issues" or "Pull Requests" or "Copilot tasks" or "Actions";
            Assert.AreEqual(eligible, PinDestination.TryParse(row.Command!.Id, out _), row.Title);
            if (eligible)
            {
                Assert.IsNotNull(fixture.Provider.GetCommandItem(row.Command.Id));
                Assert.IsNotNull(Assert.IsInstanceOfType<IExtendedAttributesProvider>(row).GetProperties()[PinItemProperties.DockCommandId]);
            }
            else { Assert.IsEmpty(row.Command.Id); }
        }
        Assert.IsEmpty(repository.Id);
        Assert.IsEmpty(fixture.Provider.GetCommand(CreateCodespacePage.PageId)!.Id);
        Assert.IsEmpty(fixture.Provider.GetCommand(IssueDetailsPage.PageId)!.Id);
        Assert.IsNull(fixture.Provider.GetCommandItem(CreateCodespacePage.PageId));
        Assert.IsNull(fixture.Provider.GetCommandItem(RepositoryIssuesPage.PageId + ".octocat%2Fhello"));
    }

    [TestMethod]
    public void Dispose_RemovesRestoredItemsAndSuppressesFurtherNotifications()
    {
        using var fixture = new Fixture(Account);
        var id = Destination(nameof(PinDestinationKind.RepositoryIssues)).Id;
        var page = Assert.IsInstanceOfType<PinnedDestinationPage>(fixture.Provider.GetCommandItem(id)!.Command);
        page.GetItems();
        fixture.Provider.Dispose();
        var changes = 0;
        page.ItemsChanged += (_, _) => changes++;
        fixture.Auth.SignOut();

        Assert.IsNull(fixture.Provider.GetCommandItem(id));
        Assert.IsNull(fixture.Provider.GetCommand(id));
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual(0, changes);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task Notifications_ReentrantHostCanReadPageFromAnotherThread()
    {
        using var fixture = new Fixture(Account);
        var page = Assert.IsInstanceOfType<PinnedDestinationPage>(
            fixture.Provider.GetCommandItem(Destination(nameof(PinDestinationKind.RepositoryIssues)).Id)!.Command);
        page.GetItems();
        await Load(page.LoadedPage!);
        var changes = 0;
        page.ItemsChanged += (_, _) =>
        {
            Task.Run(() => page.GetItems()).WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).GetAwaiter().GetResult();
            changes++;
        };
        fixture.Auth.SignOut();
        Assert.IsGreaterThan(0, changes);
    }

    [TestMethod]
    public void DockMetadata_IsAvailableAcrossWinRTForEveryItemSurface()
    {
        using var fixture = new Fixture(Account);
        var id = PinDestination.GlobalId(PinDestinationKind.Agents);
        var command = fixture.Provider.GetCommandItem(id)!.Command!;
        foreach (var item in new ICommandItem[]
        {
            new PinnableListItem(command),
            new PinnableCommandItem(command),
            new PinnableCommandContextItem(command),
            fixture.Provider.TopLevelCommands().Single(),
        })
        {
            var pointer = WinRT.MarshalInterface<ICommandItem>.FromManaged(item);
            try
            {
                var interfaceId = typeof(IExtendedAttributesProvider).GUID;
                var result = Marshal.QueryInterface(pointer, in interfaceId, out var attributesPointer);
                try
                {
                    Assert.AreEqual(0, result);
                    var attributes = WinRT.MarshalInterface<IExtendedAttributesProvider>.FromAbi(attributesPointer);
                    Assert.AreEqual(PinDestination.DockPrefix + item.Command!.Id,
                        attributes.GetProperties()[PinItemProperties.DockCommandId]);
                }
                finally
                {
                    if (attributesPointer != IntPtr.Zero) { Marshal.Release(attributesPointer); }
                }
            }
            finally { Marshal.Release(pointer); }
        }
    }

    [TestMethod]
    public async Task BoundAccount_BlocksStaleWritesBeforeAccountNotificationsReachThePage()
    {
        using var fixture = new Fixture(Account);
        AuthService? bound = null;
        MutationExecutor? mutations = null;
        Task<MutationResult<bool>>? staleWrite = null;
        var writes = 0;
        fixture.Auth.AccountChanged += (_, _) =>
        {
            Assert.IsNull(bound!.CurrentAccount);
            staleWrite = mutations!.ExecuteAsync(Account, "repository-action", _ => Task.FromResult(true), _ =>
            {
                writes++;
                return Task.FromResult(new MutationResult<bool>(MutationState.Completed, true));
            });
        };
        using (bound = fixture.Auth.ForAccount(Account.Host, Account.Login))
        using (mutations = new MutationExecutor(bound))
        {
            Assert.AreSame(Account, bound.CurrentAccount);
            await fixture.Auth.SignInWithTokenAsync("github.com", "mona", TestContext.CancellationToken);
            Assert.IsNotNull(staleWrite);
            Assert.AreEqual(MutationState.Stale, (await staleWrite).State);
            Assert.AreEqual(0, writes);
        }
    }

    [TestMethod]
    [DataRow("github.com")]
    [DataRow("https://GitHub.Example.com:8443/")]
    [DataRow("octocorp.ghe.com")]
    public void Identity_RoundTripsHostAccountAndRepositoryWithCanonicalCasing(string hostText)
    {
        Assert.IsTrue(GitHubHost.TryParse(hostText, out var host));
        var destination = new PinDestination(PinDestinationKind.RepositoryIssues, host, "User_company", "Owner/My.Repo_1");
        Assert.IsTrue(PinDestination.TryParse(destination.Id, out var restored));
        Assert.AreEqual(host, restored!.Host);
        Assert.AreEqual("user_company", restored.Login);
        Assert.AreEqual("owner/my.repo_1", restored.Repository);
        Assert.AreEqual(destination.Id, restored.Id);
        Assert.IsTrue(restored.Matches(new GitHubAccount(host, "user_company", "new-token")));
        Assert.IsFalse(restored.Matches(new GitHubAccount(host, "someone", "test-token")));
        Assert.AreNotEqual(destination.Id, (destination with { Kind = PinDestinationKind.RepositoryActions }).Id);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v2.Home")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v1.0")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v1.Home|extra")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v1.RepositoryIssues")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v1.RepositoryIssues|https%3A%2F%2Fgithub.com%2F|OCTOCAT|o%2F..")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v1.RepositoryIssues|http%3A%2F%2Fgithub.com%2F|OCTOCAT|o%2Fr")]
    [DataRow("com.baldbeardedbuilder.cmdpal.github.pin.v1.RepositoryIssues|https%3A%2F%2Fgithub.com%2F|OCTOCAT|o%2Fr")]
    public void Resolver_RejectsMalformedUnknownAndNoncanonicalIds(string id)
    {
        using var fixture = new Fixture(Account);
        Assert.IsFalse(PinDestination.TryParse(id, out var parsed));
        Assert.IsNull(parsed);
        Assert.IsNull(fixture.Provider.GetCommandItem(id));
        Assert.IsNull(fixture.Provider.GetCommandItem(PinDestination.DockPrefix + id));
        fixture.VerifyNoReads();
    }

    private static PinDestination Destination(string name)
    {
        var kind = Enum.Parse<PinDestinationKind>(name);
        return kind >= PinDestinationKind.RepositoryIssues
            ? new(kind, Account.Host, Account.Login, "octocat/hello") : new(kind);
    }

    private static Task Load(ListPage page) => page switch
    {
        NotificationsPage notifications => notifications.CurrentLoad,
        ReposPage repos => repos.CurrentLoad,
        AgentsPage agents => agents.CurrentLoad,
        CodespacesPage codespaces => codespaces.CurrentLoad,
        RepositoryIssuesPage issues => issues.CurrentLoad,
        RepositoryPullRequestsPage pulls => pulls.CurrentLoad,
        ActionsPage actions => actions.CurrentLoad,
        _ => Task.CompletedTask,
    };

    private sealed class Fixture : IDisposable
    {
        internal readonly AuthService Auth;
        internal readonly GitHubCommandsProvider Provider;
        internal readonly Mock<INotificationsClient> Notifications = new();
        internal readonly Mock<IRepositoriesClient> Repos = new();
        internal readonly Mock<IIssuesClient> Issues = new();
        internal readonly Mock<IPullRequestsClient> Pulls = new();
        internal readonly Mock<IActionsClient> Actions = new();
        internal readonly Mock<IAgentsClient> Agents = new();
        internal readonly Mock<IAgentBrowsingClient> Browsing;
        internal readonly Mock<ICodespacesClient> Codespaces = new();

        internal Fixture(GitHubAccount? account)
        {
            var identity = new Mock<IGitHubAuthClient>();
            identity.Setup(client => client.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((GitHubHost _, string token, CancellationToken _) => Task.FromResult(token));
            Auth = new(new InMemoryAccountStore(account), identity.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
            Notifications.Setup(client => client.GetNotificationsAsync(It.IsAny<GitHubAccount>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new NotificationsPageResult([], null));
            Repos.Setup(client => client.GetMyRepositoriesAsync(It.IsAny<GitHubAccount>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RepositoriesPageResult([], null));
            Issues.Setup(client => client.GetIssuesAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssuesPageResult([], null));
            Pulls.Setup(client => client.GetPullRequestsAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PullRequestsPageResult([], null));
            Actions.Setup(client => client.GetRunsAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new WorkflowRunsPageResult([], null));
            Browsing = Agents.As<IAgentBrowsingClient>();
            Browsing.Setup(client => client.GetTasksAsync(It.IsAny<GitHubAccount>(), It.IsAny<AgentQuery>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AgentTasksPageResult([], null));
            Codespaces.Setup(client => client.GetCodespacesAsync(It.IsAny<GitHubAccount>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([], null));
            Provider = new(Auth, () => "", Notifications.Object, new FakeBrowser(_ => null), Repos.Object, Issues.Object,
                Codespaces.Object, Actions.Object, Agents.Object, Pulls.Object, pullRequestActionsClient: Mock.Of<IPullRequestActionsClient>());
        }

        internal void VerifyNoReads()
        {
            Notifications.VerifyNoOtherCalls();
            Repos.VerifyNoOtherCalls();
            Issues.VerifyNoOtherCalls();
            Pulls.VerifyNoOtherCalls();
            Actions.VerifyNoOtherCalls();
            Browsing.VerifyNoOtherCalls();
            Codespaces.VerifyNoOtherCalls();
        }

        public void Dispose()
        {
            Provider.Dispose();
            Auth.Dispose();
        }
    }
}
