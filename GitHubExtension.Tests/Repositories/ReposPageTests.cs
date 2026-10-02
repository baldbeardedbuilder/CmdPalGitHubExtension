// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public class ReposPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly string[] LocalThenRemote = ["octocat/power-tools", "microsoft/PowerToys"];

    [TestMethod]
    public async Task GetItems_LoadsYourRepos()
    {
        var client = Client([RepoFormattingTests.Repo("octocat/hello", isPrivate: true)]);
        using var page = CreatePage(client.Object, out _);

        page.GetItems();
        await page.CurrentLoad;

        var item = (RepoItem)page.GetItems().Single();
        Assert.AreEqual("octocat/hello", item.Title);
        Assert.EndsWith("12m ago", item.Subtitle);
        Assert.AreEqual("Private", item.Tags[0].Text);
    }

    [TestMethod]
    public async Task Search_ShowsLocalMatchesThenGitHubResults()
    {
        var client = Client([RepoFormattingTests.Repo("octocat/power-tools"), RepoFormattingTests.Repo("octocat/other")]);
        client.Setup(c => c.SearchAsync(Account, "power", It.IsAny<CancellationToken>()))
            .ReturnsAsync([RepoFormattingTests.Repo("microsoft/PowerToys"), RepoFormattingTests.Repo("octocat/power-tools")]);
        using var page = await LoadedPage(client.Object);

        page.SearchText = "power";
        await page.CurrentSearch;

        CollectionAssert.AreEqual(LocalThenRemote, page.GetItems().Select(i => i.Title).ToArray());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task Search_MatchesDescriptionAndLanguage()
    {
        var client = Client(
        [
            RepoFormattingTests.Repo("o/a", language: "Rust", description: "Fast thing"),
            RepoFormattingTests.Repo("o/b", language: "Go", description: "Fast too"),
        ]);
        client.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var page = await LoadedPage(client.Object);

        page.SearchText = "fast rust";

        Assert.AreEqual("o/a", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task SearchFailure_ShowsTheError()
    {
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "x", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        using var page = await LoadedPage(client.Object);

        page.SearchText = "x";
        await page.CurrentSearch;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("rate limited", page.EmptyContent!.Subtitle);
    }

    [TestMethod]
    public async Task ClearingSearch_RestoresYourRepos()
    {
        var client = Client([RepoFormattingTests.Repo("o/a"), RepoFormattingTests.Repo("o/b")]);
        client.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        using var page = await LoadedPage(client.Object);

        page.SearchText = "o/a";
        await page.CurrentSearch;
        page.SearchText = string.Empty;

        Assert.HasCount(2, page.GetItems());
    }

    [TestMethod]
    public async Task LoadMore_AppendsTheNextPage()
    {
        var next = new Uri("https://api.github.com/user/repos?page=2");
        var client = new Mock<IRepositoriesClient>();
        client.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult([RepoFormattingTests.Repo("o/a")], next));
        client.Setup(c => c.GetMyRepositoriesAsync(Account, next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult([RepoFormattingTests.Repo("o/b")], null));
        using var page = await LoadedPage(client.Object);
        Assert.IsTrue(page.HasMoreItems);

        page.LoadMore();
        await page.CurrentLoad;

        Assert.HasCount(2, page.GetItems());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task Open_LaunchesTheRepoInTheBrowser()
    {
        using var page = CreatePage(Client([RepoFormattingTests.Repo("o/a")]).Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;

        ((InvokableCommand)page.GetItems().Single().Command!).Invoke();

        Assert.AreEqual(new Uri("https://github.com/o/a"), browser.LastOpened);
    }

    [TestMethod]
    public async Task LoadFailure_ShowsTheError()
    {
        var client = new Mock<IRepositoriesClient>();
        client.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("nope"));
        using var page = await LoadedPage(client.Object);

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("nope", page.EmptyContent!.Subtitle);
    }

    private static Mock<IRepositoriesClient> Client(GitHubRepository[] repos)
    {
        var client = new Mock<IRepositoriesClient>();
        client.Setup(c => c.GetMyRepositoriesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult(repos, null));
        return client;
    }

    private static async Task<ReposPage> LoadedPage(IRepositoriesClient client)
    {
        var page = CreatePage(client, out _);
        page.GetItems();
        await page.CurrentLoad;
        return page;
    }

    private static ReposPage CreatePage(IRepositoriesClient client, out FakeBrowser browser)
    {
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.GetUtcNow()).Returns(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));
        return new ReposPage(auth, client, browser, time.Object, TimeSpan.Zero);
    }
}
