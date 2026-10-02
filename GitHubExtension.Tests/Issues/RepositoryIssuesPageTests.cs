// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class RepositoryIssuesPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedLabels = ["bug", "command-palette"];
    private static readonly string[] ExpectedPagedTitles = ["#1 First", "#2 Second"];

    [TestMethod]
    public async Task Open_ShowsIssueStateMetadataAndLabels()
    {
        var issue = CreateIssue(42, "Keyboard navigation", SubjectState.Open, "htcfreek", 3, ["bug", "command-palette"]);
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([issue], null));
        using var page = CreatePage(client.Object, out _);

        page.Open("octo/tool");
        await page.CurrentLoad;

        var item = Assert.IsInstanceOfType<RepositoryIssueItem>(page.GetItems().Single());
        Assert.AreEqual("#42 Keyboard navigation", item.Title);
        Assert.AreEqual("opened 45m ago by htcfreek · 3 comments", item.Subtitle);
        Assert.AreSame(Icons.StateOpenIssue, item.Icon);
        CollectionAssert.AreEqual(ExpectedLabels, item.Tags.Select(tag => tag.Text).ToArray());
        Assert.AreEqual("octo/tool issues", page.Title);
    }

    [TestMethod]
    public async Task Open_UsesClosedAndNotPlannedIssueStateIcons()
    {
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult(
            [
                CreateIssue(1, "Closed", SubjectState.Closed, null, 0, []),
                CreateIssue(2, "Not planned", SubjectState.NotPlanned, null, 0, []),
            ],
            null));
        using var page = CreatePage(client.Object, out _);

        page.Open("octo/tool");
        await page.CurrentLoad;

        var items = page.GetItems().Cast<RepositoryIssueItem>().ToArray();
        Assert.AreSame(Icons.StateClosedIssue, items[0].Icon);
        Assert.AreSame(Icons.StateNotPlanned, items[1].Icon);
    }

    [TestMethod]
    public async Task Search_FiltersByTitleAuthorAndLabel()
    {
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult(
            [
                CreateIssue(1, "Keyboard issue", SubjectState.Open, "octocat", 0, ["bug"]),
                CreateIssue(2, "Improve docs", SubjectState.Open, "mona", 0, ["documentation"]),
            ],
            null));
        using var page = CreatePage(client.Object, out _);
        page.Open("octo/tool");
        await page.CurrentLoad;

        page.SearchText = "keyboard bug";
        Assert.AreEqual("#1 Keyboard issue", page.GetItems().Single().Title);

        page.SearchText = "mona";
        Assert.AreEqual("#2 Improve docs", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task LoadMore_FollowsNextPageAndKeepsItems()
    {
        var next = new Uri("https://api.github.com/repos/octo/tool/issues?page=2");
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([CreateIssue(1, "First", SubjectState.Open, null, 0, [])], next));
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([CreateIssue(2, "Second", SubjectState.Closed, null, 0, [])], null));
        using var page = CreatePage(client.Object, out _);
        page.Open("octo/tool");
        await page.CurrentLoad;
        Assert.IsTrue(page.HasMoreItems);

        page.SearchText = "First";
        Assert.IsFalse(page.HasMoreItems);
        page.SearchText = string.Empty;
        Assert.IsTrue(page.HasMoreItems);
        page.LoadMore();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(ExpectedPagedTitles, page.GetItems().Select(item => item.Title).ToArray());
        Assert.IsFalse(page.HasMoreItems);
        client.Verify(c => c.GetIssuesAsync(Account, "octo/tool", next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmptyRepository_ShowsEmptyState()
    {
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([], null));
        using var page = CreatePage(client.Object, out _);
        page.Open("octo/tool");
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No issues found", page.EmptyContent!.Title);
        Assert.AreEqual("octo/tool doesn't have any issues", page.EmptyContent.Subtitle);
    }

    [TestMethod]
    public async Task LoadFailure_RefreshRetriesAndRecovers()
    {
        var client = new Mock<IIssuesClient>();
        client.SetupSequence(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"))
            .ReturnsAsync(new IssuesPageResult([CreateIssue(1, "Recovered", SubjectState.Open, null, 0, [])], null));
        using var page = CreatePage(client.Object, out _);
        page.Open("octo/tool");
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        var error = Assert.IsInstanceOfType<CommandItem>(page.EmptyContent);
        Assert.AreEqual("Couldn't load issues", error.Title);
        Assert.AreEqual("rate limited", error.Subtitle);

        var refresh = Assert.IsInstanceOfType<RefreshRepositoryItemsCommand>(error.Command);
        refresh.Invoke();
        await page.CurrentLoad;

        Assert.AreEqual("#1 Recovered", page.GetItems().Single().Title);
        client.Verify(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task SignOut_ClearsLoadedIssues()
    {
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([CreateIssue(1, "Private issue", SubjectState.Open, null, 0, [])], null));
        using var page = CreatePage(client.Object, out _, out var auth);
        page.Open("octo/tool");
        await page.CurrentLoad;
        Assert.AreEqual("#1 Private issue", page.GetItems().Single().Title);

        new SignOutCommand(auth).Invoke();

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Choose a repository", page.EmptyContent!.Title);
    }

    private static GitHubIssue CreateIssue(
        int number,
        string title,
        SubjectState state,
        string? author,
        int comments,
        string[] labels) =>
        new(
            number,
            title,
            null,
            state,
            new Uri($"https://github.com/octo/tool/issues/{number}"),
            Now.AddMinutes(-45),
            author,
            [],
            labels,
            comments);

    private static RepositoryIssuesPage CreatePage(IIssuesClient client, out FakeBrowser browser) =>
        CreatePage(client, out browser, out _);

    private static RepositoryIssuesPage CreatePage(IIssuesClient client, out FakeBrowser browser, out AuthService auth)
    {
        auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        var time = new Mock<TimeProvider>();
        time.Setup(provider => provider.GetUtcNow()).Returns(Now);
        return new RepositoryIssuesPage(auth, client, browser, time.Object);
    }
}
