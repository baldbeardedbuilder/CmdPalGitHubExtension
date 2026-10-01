// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests;

[TestClass]
public class GitHubCommandsProviderTests
{
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
    public void HomePage_ShowsAccountAndSignOut()
    {
        var store = new InMemoryAccountStore(new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "t"));
        using var provider = CreateProvider(store, out _);

        var items = ((HomePage)provider.GetCommand(HomePage.PageId)!).GetItems();

        Assert.HasCount(2, items);
        Assert.AreEqual("Signed in as @octocat", items[0].Title);
        Assert.IsInstanceOfType<SignOutCommand>(items[1].Command);
    }

    private static GitHubCommandsProvider CreateProvider(InMemoryAccountStore store, out AuthService auth)
    {
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("mona");
        auth = new AuthService(store, client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        return new GitHubCommandsProvider(auth, () => string.Empty);
    }
}
