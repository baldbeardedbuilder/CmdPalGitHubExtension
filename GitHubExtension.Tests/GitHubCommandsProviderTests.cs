// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
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
        Assert.IsTrue(items.Where((_, i) => i is 1 or 4).All(i => i.Command is NoOpCommand));
        Assert.IsTrue(items.All(i => i.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is SignOutCommand)));
    }

    [TestMethod]
    public void GetCommand_ResolvesNotificationsPage()
    {
        using var provider = CreateProvider(new InMemoryAccountStore(), out _);

        Assert.IsInstanceOfType<NotificationsPage>(provider.GetCommand(NotificationsPage.PageId));
        Assert.IsInstanceOfType<IssueDetailsPage>(provider.GetCommand(IssueDetailsPage.PageId));
        Assert.IsInstanceOfType<ReposPage>(provider.GetCommand(ReposPage.PageId));
        Assert.IsInstanceOfType<AgentsPage>(provider.GetCommand(AgentsPage.PageId));
    }

    private static GitHubCommandsProvider CreateProvider(InMemoryAccountStore store, out AuthService auth)
    {
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("mona");
        auth = new AuthService(store, client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        return new GitHubCommandsProvider(auth, () => string.Empty, Mock.Of<INotificationsClient>(), new FakeBrowser(_ => null), Mock.Of<IRepositoriesClient>(), agentsClient: Mock.Of<IAgentsClient>());
    }
}
