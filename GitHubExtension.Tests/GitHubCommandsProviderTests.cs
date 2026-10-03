// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using BaldBeardedBuilder.CmdPal.GitHub.Tests.Notifications;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests;

[TestClass]
public class GitHubCommandsProviderTests
{
    private static readonly string[] HomeSections = ["Notifications", "Saved Queries", "Repos", "Agents", "Codespaces"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SignedOut_TopLevelOpensSignInPage()
    {
        using var provider = CreateProvider(new InMemoryAccountStore(), out _);

        var item = provider.TopLevelCommands().Single();

        Assert.AreEqual("GitHub", item.Title);
        Assert.IsInstanceOfType<SignInPage>(item.Command);
    }

    [TestMethod]
    public void SignedIn_TopLevelOpensHomePage()
    {
        var store = new InMemoryAccountStore(new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "t"));
        using var provider = CreateProvider(store, out _);

        var item = provider.TopLevelCommands().Single();

        Assert.IsInstanceOfType<HomePage>(item.Command);
        Assert.Contains("@octocat", item.Subtitle);
    }

    [TestMethod]
    public async Task SigningInAndOut_SwapsTopLevelCommand()
    {
        using var provider = CreateProvider(new InMemoryAccountStore(), out var auth);
        var changes = 0;
        provider.ItemsChanged += (_, _) => changes++;

        await auth.SignInWithTokenAsync("github.example.com", "ghp_x", TestContext.CancellationToken);
        Assert.IsInstanceOfType<HomePage>(provider.TopLevelCommands().Single().Command);

        new SignOutCommand(auth).Invoke();
        Assert.IsInstanceOfType<SignInPage>(provider.TopLevelCommands().Single().Command);
        Assert.AreEqual(2, changes);
    }

    [TestMethod]
    public void GetCommand_ResolvesPagesById()
    {
        using var provider = CreateProvider(new InMemoryAccountStore(), out _);

        Assert.IsInstanceOfType<SignInPage>(provider.GetCommand(SignInPage.PageId));
        Assert.IsInstanceOfType<HomePage>(provider.GetCommand(HomePage.PageId));
        Assert.IsNull(provider.GetCommand("nope"));
    }

    [TestMethod]
    public void HomePage_ShowsSections()
    {
        var store = new InMemoryAccountStore(new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "t"));
        using var provider = CreateProvider(store, out _);

        var items = ((HomePage)provider.GetCommand(HomePage.PageId)!).GetItems();

        CollectionAssert.AreEqual(
            HomeSections,
            items.Select(i => i.Title).ToArray());
        Assert.IsInstanceOfType<NotificationsPage>(items[0].Command);
        Assert.IsInstanceOfType<ReposPage>(items[2].Command);
        Assert.IsInstanceOfType<AgentsPage>(items[3].Command);
        Assert.IsInstanceOfType<CodespacesPage>(items[4].Command);
        var create = items[4].MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is CreateCodespacePage);
        Assert.AreSame(provider.GetCommand(CreateCodespacePage.PageId), create.Command);
        Assert.AreEqual("Create Codespace", create.Command!.Name);
        Assert.IsInstanceOfType<IssueSearchPage>(items[1].Command);
        Assert.AreSame(provider.GetCommand(IssueSearchPage.PageId), items[1].Command);
        Assert.IsTrue(items.All(i => i.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is SignOutCommand)));
    }

    [TestMethod]
    public void HomePage_ExposesStarredRepositoriesWhenClientIsAvailable()
    {
        var store = new InMemoryAccountStore(new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "t"));
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("mona");
        var auth = new AuthService(store, client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var provider = new GitHubCommandsProvider(
            auth,
            () => string.Empty,
            browser: new FakeBrowser(_ => null),
            repositoriesClient: Mock.Of<IRepositoriesClient>(),
            agentsClient: Mock.Of<IAgentsClient>(),
            repositoryStarsClient: Mock.Of<IRepositoryStarsClient>());

        var homeItems = ((HomePage)provider.GetCommand(HomePage.PageId)!).GetItems();
        var starred = homeItems.Single(item => item.Title == "Starred repositories");

        Assert.IsInstanceOfType<ReposPage>(starred.Command);
        Assert.AreSame(starred.Command, provider.GetCommand(ReposPage.StarredPageId));
    }

    [TestMethod]
    public void GetCommand_ResolvesNotificationsPage()
    {
        using var provider = CreateProvider(new InMemoryAccountStore(), out _);

        Assert.IsInstanceOfType<NotificationsPage>(provider.GetCommand(NotificationsPage.PageId));
        Assert.IsInstanceOfType<IssueDetailsPage>(provider.GetCommand(IssueDetailsPage.PageId));
        Assert.IsInstanceOfType<RepositoryIssuesPage>(provider.GetCommand(RepositoryIssuesPage.PageId));
        Assert.IsInstanceOfType<RepositoryPullRequestsPage>(provider.GetCommand(RepositoryPullRequestsPage.PageId));
        Assert.IsInstanceOfType<ReposPage>(provider.GetCommand(ReposPage.PageId));
        Assert.IsInstanceOfType<AgentsPage>(provider.GetCommand(AgentsPage.PageId));
        Assert.IsInstanceOfType<CodespacesPage>(provider.GetCommand(CodespacesPage.PageId));
        Assert.IsInstanceOfType<CreateCodespacePage>(provider.GetCommand(CreateCodespacePage.PageId));
        Assert.IsInstanceOfType<IssueSearchPage>(provider.GetCommand(IssueSearchPage.PageId));
    }

    [TestMethod]
    public void Dispose_RemovesSavedQueryCommand()
    {
        using var provider = CreateProvider(new InMemoryAccountStore(), out _);
        Assert.IsInstanceOfType<IssueSearchPage>(provider.GetCommand(IssueSearchPage.PageId));

        provider.Dispose();

        Assert.IsNull(provider.GetCommand(IssueSearchPage.PageId));
    }

    [TestMethod]
    public async Task Dispose_CancelsNotificationWriteAndSuppressesLateResult()
    {
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "t");
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1")], null));
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.MarkAsReadAsync(account, "1", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return finish.Task;
            });
        using var provider = CreateProvider(new InMemoryAccountStore(account), out _, client.Object);
        var page = (NotificationsPage)provider.GetCommand(NotificationsPage.PageId)!;
        page.GetItems();
        await page.CurrentLoad;
        page.MarkAsRead((NotificationItem)page.GetItems().Single());
        var token = await started.Task.WaitAsync(TestContext.CancellationToken);

        provider.Dispose();
        Assert.IsTrue(token.IsCancellationRequested);
        var changes = 0;
        page.ItemsChanged += (_, _) => changes++;
        finish.SetResult();
        await page.CurrentMutation;

        Assert.AreEqual(0, changes);
    }

    private static GitHubCommandsProvider CreateProvider(InMemoryAccountStore store, out AuthService auth, INotificationsClient? notificationsClient = null)
    {
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("mona");
        auth = new AuthService(store, client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        return new GitHubCommandsProvider(auth, () => string.Empty, notificationsClient ?? Mock.Of<INotificationsClient>(), new FakeBrowser(_ => null), Mock.Of<IRepositoriesClient>(), agentsClient: Mock.Of<IAgentsClient>());
    }
}
