// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class RepositoryPullRequestsPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ExpectedStateTags = ["Draft", "Merged", "Closed"];
    private static readonly string[] ExpectedPagedTitles = ["#1 First", "#2 Second"];
    private static readonly string[] ExpectedPullRequestFilters = ["Open", "Closed"];
    private static readonly string[] ExpectedOpenPullRequestTitles = ["#1 Open", "#2 Draft"];
    private static readonly string[] ExpectedClosedPullRequestTitles = ["#3 Closed", "#4 Merged", "#5 Closed draft"];

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
    public void PullRequestItems_ShowStateBadges()
    {
        var browser = new FakeBrowser(_ => null);
        var items = new[]
        {
            new RepositoryPullRequestItem(CreatePullRequest(1, "Draft", SubjectState.Draft, null, "draft", "main", []), browser, Now),
            new RepositoryPullRequestItem(CreatePullRequest(2, "Merged", SubjectState.Merged, null, "merged", "main", []), browser, Now),
            new RepositoryPullRequestItem(CreatePullRequest(3, "Closed", SubjectState.Closed, null, "closed", "main", []), browser, Now),
            new RepositoryPullRequestItem(CreatePullRequest(4, "Unknown", SubjectState.Unknown, null, "unknown", "main", []), browser, Now),
        };

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

        page.Filters!.CurrentFilterId = PullRequestFilters.Closed;
        page.SearchText = "mona";
        Assert.AreEqual("#2 Fix docs", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task Filters_SelectOpenOrClosedPullRequestsAndDefaultToOpen()
    {
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
                ParsePullRequests(
                    """
                    [
                      {"number":1,"title":"Open","state":"open","draft":false,"html_url":"https://github.com/octo/tool/pull/1","head":{},"base":{}},
                      {"number":2,"title":"Draft","state":"open","draft":true,"html_url":"https://github.com/octo/tool/pull/2","head":{},"base":{}},
                      {"number":3,"title":"Closed","state":"closed","html_url":"https://github.com/octo/tool/pull/3","head":{},"base":{}},
                      {"number":4,"title":"Merged","state":"closed","merged_at":"2025-01-01T00:00:00Z","html_url":"https://github.com/octo/tool/pull/4","head":{},"base":{}},
                      {"number":5,"title":"Closed draft","state":"closed","draft":true,"html_url":"https://github.com/octo/tool/pull/5","head":{},"base":{}}
                    ]
                    """),
                null));
        using var page = CreatePage(client.Object);
        page.Open("octo/tool");
        await page.CurrentLoad;

        var filters = Assert.IsInstanceOfType<PullRequestFilters>(page.Filters);
        var filterItems = filters.GetFilters().Cast<Filter>().ToArray();
        CollectionAssert.AreEqual(
            ExpectedPullRequestFilters,
            filterItems.Select(filter => filter.Name).ToArray());
        Assert.IsNotNull(filterItems[0].Icon);
        Assert.IsNotNull(filterItems[1].Icon);
        Assert.AreEqual(PullRequestFilters.Open, filters.CurrentFilterId);
        CollectionAssert.AreEqual(
            ExpectedOpenPullRequestTitles,
            page.GetItems().Select(item => item.Title).ToArray());

        filters.CurrentFilterId = PullRequestFilters.Closed;
        CollectionAssert.AreEqual(
            ExpectedClosedPullRequestTitles,
            page.GetItems().Select(item => item.Title).ToArray());
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
                [CreatePullRequest(2, "Second", SubjectState.Open, null, "second", "main", [])],
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
        Assert.AreEqual("No matching pull requests", page.EmptyContent!.Title);
        Assert.AreEqual("octo/tool doesn't have any open pull requests", page.EmptyContent.Subtitle);
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

    [TestMethod]
    public async Task MergeMenu_NavigatesToConfirmationAndRefreshInvalidatesIt()
    {
        var client = new Mock<IPullRequestsClient>();
        client.Setup(c => c.GetPullRequestsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult(
            [
                CreatePullRequest(42, "Ready", SubjectState.Open, null, "feature", "main", []),
                CreatePullRequest(43, "Draft", SubjectState.Draft, null, "feature", "main", []),
            ], null));
        var mergeClient = new Mock<IPullRequestMergeClient>();
        mergeClient.Setup(c => c.GetTargetAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestMergeTarget("octo/tool", 42, "main", "abc123", ["squash"]));
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new RepositoryPullRequestsPage(auth, client.Object, new FakeBrowser(_ => null), mergeClient: mergeClient.Object);
        page.Open("octo/tool");
        await page.CurrentLoad;
        var ready = page.GetItems().First();
        var mergePage = Assert.IsInstanceOfType<MergePullRequestPage>(
            ready.MoreCommands.Cast<CommandContextItem>().Single(c => c.Command is MergePullRequestPage).Command);
        Assert.IsInstanceOfType<OpenInBrowserCommand>(ready.Command);
        Assert.IsFalse(page.GetItems().Last().MoreCommands.Cast<CommandContextItem>().Any(c => c.Command is MergePullRequestPage));
        mergePage.GetContent();
        await mergePage.CurrentWork;
        Assert.Contains("abc123", ((IFormContent)mergePage.GetContent()[0]).TemplateJson);

        await page.RefreshAsync();

        Assert.Contains("no longer active", ((IFormContent)mergePage.GetContent()[0]).TemplateJson);
        mergeClient.Verify(c => c.MergeAsync(It.IsAny<GitHubAccount>(), It.IsAny<PullRequestMergeTarget>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

    private static List<GitHubPullRequest> ParsePullRequests(string payload)
    {
        using var json = System.Text.Json.JsonDocument.Parse(payload);
        return PullRequestsClient.ParsePullRequests(json.RootElement);
    }

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
