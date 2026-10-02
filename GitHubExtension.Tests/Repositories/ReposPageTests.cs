// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public class ReposPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly string[] LocalThenRemote = ["octocat/power-tools", "microsoft/PowerToys"];
    private static readonly string[] RepositorySections = ["o/a", "Issues", "Pull Requests", "Actions", "Discussions"];

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
    public async Task Open_ShowsRepositoryMenuWithoutLaunchingBrowser()
    {
        using var page = CreatePage(Client([RepoFormattingTests.Repo("o/a")]).Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;

        ((InvokableCommand)page.GetItems().Single().Command!).Invoke();

        Assert.IsNull(browser.LastOpened);
        Assert.AreEqual("o/a", page.RepositoryPage.Title);
        Assert.AreEqual("Search in o/a...", page.RepositoryPage.PlaceholderText);
        CollectionAssert.AreEqual(
            RepositorySections,
            page.RepositoryPage.GetItems().Select(i => i.Title).ToArray());
    }

    [TestMethod]
    public async Task RepoMoreMenu_StillOpensRepositoryInBrowser()
    {
        using var page = CreatePage(Client([RepoFormattingTests.Repo("o/a")]).Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;
        var command = page.GetItems().Single().MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is OpenInBrowserCommand open && open.Url.AbsolutePath == "/o/a");

        ((InvokableCommand)command.Command!).Invoke();

        Assert.AreEqual(new Uri("https://github.com/o/a"), browser.LastOpened);
    }

    [TestMethod]
    [DataRow("Issues", "issues")]
    [DataRow("Pull Requests", "pulls")]
    [DataRow("Discussions", "discussions")]
    [DataRow("Actions", "actions")]
    public void RepositoryMenu_OpensSectionOnRepositoryHost(string section, string path)
    {
        var browser = new FakeBrowser(_ => null);
        var page = new RepositoryPage(browser, null);
        var repository = RepoFormattingTests.Repo("o/a") with { WebUrl = new Uri("https://github.example.com/o/a/") };
        page.OpenRepository(repository);

        ((InvokableCommand)page.GetItems().Single(i => i.Title == section).Command!).Invoke();

        Assert.AreEqual(new Uri($"https://github.example.com/o/a/{path}"), browser.LastOpened);
    }

    [TestMethod]
    public void RepositoryMenu_SwitchingRepositoriesReplacesContextAndClearsSearch()
    {
        var browser = new FakeBrowser(_ => null);
        var page = new RepositoryPage(browser, null);
        page.OpenRepository(RepoFormattingTests.Repo("o/a"));
        page.SearchText = "issues";
        page.OpenRepository(RepoFormattingTests.Repo("o/b", description: "Another repo"));

        Assert.AreEqual(string.Empty, page.SearchText);
        Assert.AreEqual("o/b", page.Title);
        var overview = page.GetItems()[0];
        Assert.AreEqual("o/b", overview.Title);
        Assert.AreEqual("Another repo", overview.Subtitle);
        ((InvokableCommand)overview.Command!).Invoke();
        Assert.AreEqual(new Uri("https://github.com/o/b"), browser.LastOpened);
        foreach (var item in page.GetItems())
        {
            var open = item.MoreCommands.OfType<CommandContextItem>()
                .Select(c => c.Command).OfType<OpenInBrowserCommand>().Single();
            Assert.AreEqual(new Uri("https://github.com/o/b"), open.Url);
        }
    }

    [TestMethod]
    public async Task SearchResult_OpensRepositoryMenu()
    {
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "remote", It.IsAny<CancellationToken>()))
            .ReturnsAsync([RepoFormattingTests.Repo("o/remote")]);
        using var page = await LoadedPage(client.Object);
        page.SearchText = "remote";
        await page.CurrentSearch;

        ((InvokableCommand)page.GetItems().Single().Command!).Invoke();

        Assert.AreEqual("o/remote", page.RepositoryPage.Title);
        Assert.AreEqual("o/remote", page.RepositoryPage.GetItems()[0].Title);
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
