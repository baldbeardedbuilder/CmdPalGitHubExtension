// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class RepositoryPullRequestsPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedStateTags = ["Draft", "Merged", "Closed"];
    private static readonly string[] ExpectedPagedTitles = ["#1 First", "#2 Second"];

    [TestMethod]
    public async Task Open_ShowsBranchSubtitleAuthorAndOpenBadge()
    {
        var pullRequest = CreatePullRequest(42, "Add extension", SubjectState.Open, "andre akn", "feature/gh-extension", "main", ["enhancement"]);
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult([pullRequest], null));
        using var page = CreatePage(client.Object);

        page.Open("octo/tool");
        await page.CurrentLoad;

        var item = Assert.IsInstanceOfType<RepositoryPullRequestItem>(page.GetItems().Single());
        Assert.AreEqual("#42 Add extension", item.Title);
        Assert.AreEqual("feature/gh-extension → main · 45m ago by andre akn", item.Subtitle);
        Assert.AreEqual("Open", item.Tags.Single().Text);
        Assert.IsNotNull(item.Tags.Single().Icon);
        Assert.AreEqual("octo/tool pull requests", page.Title);
    }

    [TestMethod]
    public async Task Open_ShowsDraftMergedAndClosedBadges()
    {
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
            [
                CreatePullRequest(1, "Draft", SubjectState.Draft, null, "draft", "main", []),
                CreatePullRequest(2, "Merged", SubjectState.Merged, null, "merged", "main", []),
                CreatePullRequest(3, "Closed", SubjectState.Closed, null, "closed", "main", []),
                CreatePullRequest(4, "Unknown", SubjectState.Unknown, null, "unknown", "main", []),
            ],
            null));
        using var page = CreatePage(client.Object);
        page.Open("octo/tool");
        await page.CurrentLoad;

        var items = page.GetItems().Cast<RepositoryPullRequestItem>().ToArray();
        CollectionAssert.AreEqual(ExpectedStateTags, items.Take(3).Select(item => item.Tags.Single().Text).ToArray());
        Assert.IsEmpty(items[3].Tags);
    }

    [TestMethod]
    public async Task Search_FiltersByTitleBranchAndLabel()
    {
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
            [
                CreatePullRequest(1, "Add feature", SubjectState.Open, "octocat", "feature/search", "main", ["enhancement"]),
                CreatePullRequest(2, "Fix docs", SubjectState.Closed, "mona", "docs", "main", ["documentation"]),
            ],
            null));
        using var page = CreatePage(client.Object);
        page.Open("octo/tool");
        await page.CurrentLoad;

        page.SearchText = "feature enhancement";
        Assert.AreEqual("#1 Add feature", page.GetItems().Single().Title);

        page.SearchText = "mona";
        Assert.AreEqual("#2 Fix docs", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task LoadMore_FollowsNextPageAndKeepsItems()
    {
        var next = new Uri("https://api.github.com/repos/octo/tool/pulls?page=2");
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
                [CreatePullRequest(1, "First", SubjectState.Open, null, "first", "main", [])],
                next));
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
                [CreatePullRequest(2, "Second", SubjectState.Merged, null, "second", "main", [])],
                null));
        using var page = CreatePage(client.Object);
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
        client.Verify(c => c.GetPullRequestsAsync(Account, "octo/tool", next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmptyRepository_ShowsEmptyState()
    {
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult([], null));
        using var page = CreatePage(client.Object);
        page.Open("octo/tool");
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No pull requests found", page.EmptyContent!.Title);
        Assert.AreEqual("octo/tool doesn't have any pull requests", page.EmptyContent.Subtitle);
    }

    [TestMethod]
    public async Task LoadFailure_RefreshRetriesAndRecovers()
    {
        var client = new Mock<IPullRequestsClient>();
        client.SetupSequence(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"))
            .ReturnsAsync(new PullRequestsPageResult(
                [CreatePullRequest(1, "Recovered", SubjectState.Open, null, "feature", "main", [])],
                null));
        using var page = CreatePage(client.Object);
        page.Open("octo/tool");
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        var error = Assert.IsInstanceOfType<CommandItem>(page.EmptyContent);
        Assert.AreEqual("Couldn't load pull requests", error.Title);
        Assert.AreEqual("rate limited", error.Subtitle);

        var refresh = Assert.IsInstanceOfType<RefreshRepositoryItemsCommand>(error.Command);
        refresh.Invoke();
        await page.CurrentLoad;

        Assert.AreEqual("#1 Recovered", page.GetItems().Single().Title);
        client.Verify(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task SignOut_ClearsLoadedPullRequests()
    {
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
                [CreatePullRequest(1, "Private pull request", SubjectState.Open, null, "feature", "main", [])],
                null));
        using var page = CreatePage(client.Object, out var auth);
        page.Open("octo/tool");
        await page.CurrentLoad;
        Assert.AreEqual("#1 Private pull request", page.GetItems().Single().Title);

        new SignOutCommand(auth).Invoke();

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Choose a repository", page.EmptyContent!.Title);
    }

    private static GitHubPullRequest CreatePullRequest(
        int number,
        string title,
        SubjectState state,
        string? author,
        string? head,
        string? @base,
        string[] labels) =>
        new()
        {
            Number = number,
            Title = title,
            WebUrl = new Uri($"https://github.com/octo/tool/pull/{number}"),
            State = state,
            Author = author,
            HeadBranch = head,
            BaseBranch = @base,
            HeadRef = head,
            BaseRef = @base,
            Labels = labels,
            CreatedAt = Now.AddMinutes(-45),
        };

    private static RepositoryPullRequestsPage CreatePage(IPullRequestsClient client) =>
        CreatePage(client, out _);

    private static RepositoryPullRequestsPage CreatePage(IPullRequestsClient client, out AuthService auth)
    {
        auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var time = new Mock<TimeProvider>();
        time.Setup(provider => provider.GetUtcNow()).Returns(Now);
        return new RepositoryPullRequestsPage(auth, client, new FakeBrowser(_ => null), time.Object);
    }
}
