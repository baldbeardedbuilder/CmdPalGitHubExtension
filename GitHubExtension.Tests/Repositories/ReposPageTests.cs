// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public class ReposPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly string[] LocalThenRemote = ["octocat/power-tools", "microsoft/PowerToys"];
    private static readonly string[] RepositorySections = ["o/a", "Issues", "Pull Requests", "Actions", "Discussions"];
    private static readonly string[] RepositorySectionsForToolkit = ["octocat/toolkit", "Issues", "Pull Requests", "Actions", "Discussions"];
    private static readonly string[] RepositoryBrowserActions = ["Open issues", "Open pull requests"];

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
        client.Setup(c => c.SearchAsync(Account, "power", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("microsoft/PowerToys"), RepoFormattingTests.Repo("octocat/power-tools")], null, 2));
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
        client.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([], null, 0));
        using var page = await LoadedPage(client.Object);

        page.SearchText = "fast rust";
        await page.CurrentSearch;

        Assert.AreEqual("o/a", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task SearchFailure_ShowsTheError()
    {
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "x", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        using var page = await LoadedPage(client.Object);

        page.SearchText = "x";
        await page.CurrentSearch;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("rate limited", page.EmptyContent!.Subtitle);
    }

    [TestMethod]
    public async Task SearchTimeout_ClearsLoadingAndRefreshRetries()
    {
        var client = Client([]);
        client.SetupSequence(c => c.SearchAsync(Account, "remote", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("transport timeout"))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/remote")], null, 1));
        using var page = await LoadedPage(client.Object);

        page.SearchText = "remote";
        await page.CurrentSearch;

        Assert.IsFalse(page.IsLoading);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Couldn't search GitHub", page.EmptyContent!.Title);
        Assert.AreEqual("GitHub took too long to respond. Try searching again.", page.EmptyContent.Subtitle);
        ((InvokableCommand)page.EmptyContent.Command!).Invoke();
        await page.CurrentSearch;
        await page.CurrentLoad;

        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("o/remote", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task SearchCancellation_RemainsQuietAndDoesNotOverwriteNewSearch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "old", null, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, Uri? _, CancellationToken token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new RepositorySearchPageResult([], null, 0);
            });
        client.Setup(c => c.SearchAsync(Account, "new", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/new")], null, 1));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "old";
        var oldSearch = page.CurrentSearch;
        await started.Task;

        page.SearchText = "new";
        await page.CurrentSearch;
        await oldSearch;

        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("o/new", page.GetItems().Single().Title);
        Assert.AreNotEqual("Couldn't search GitHub", page.EmptyContent!.Title);
    }

    [TestMethod]
    public async Task ClearingSearch_RestoresYourRepos()
    {
        var client = Client([RepoFormattingTests.Repo("o/a"), RepoFormattingTests.Repo("o/b")]);
        client.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([], null, 0));
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
    public async Task Open_NavigatesToRepositoryPageWithoutLaunchingBrowser()
    {
        using var page = CreatePage(Client([RepoFormattingTests.Repo("o/a")]).Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;

        var repository = Assert.IsInstanceOfType<RepositoryPage>(page.GetItems().Single().Command);

        Assert.IsNull(browser.LastOpened);
        Assert.AreEqual("o/a", repository.Title);
        Assert.AreEqual("Search in o/a...", repository.PlaceholderText);
        CollectionAssert.AreEqual(RepositorySections, repository.GetItems().Select(i => i.Title).ToArray());
    }

    [TestMethod]
    public void RepositoryMenu_OffersAgentTaskCreationWhenClientIsAvailable()
    {
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new RepositoryPage(new FakeBrowser(_ => null), null, RepoFormattingTests.Repo("octocat/hello"),
            auth: auth, agentsClient: Mock.Of<IAgentsClient>());

        var create = Assert.IsInstanceOfType<CreateAgentTaskPage>(
            page.GetItems().Single(item => item.Title == "Start Copilot task").Command);

        Assert.AreEqual("Start Agent Task", create.Name);
        Assert.Contains("octocat%2Fhello", create.Id);
    }

    [TestMethod]
    public async Task RepoMoreMenu_StillOpensRepositoryInBrowser()
    {
        using var page = CreatePage(Client([RepoFormattingTests.Repo("o/a")]).Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;
        var command = page.GetItems().Single().MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is OpenInBrowserCommand open && open.Url.AbsolutePath == "/o/a");

        var result = ((InvokableCommand)command.Command!).Invoke();

        Assert.AreEqual(new Uri("https://github.com/o/a"), browser.LastOpened);
        Assert.AreEqual(CommandResultKind.Dismiss, result.Kind);
    }

    [TestMethod]
    public async Task RepositoryContextActions_PreserveBrowserLinksForIssuesAndPullRequests()
    {
        using var page = CreatePage(Client([RepoFormattingTests.Repo("octocat/toolkit")]).Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;

        var item = Assert.IsInstanceOfType<RepoItem>(page.GetItems().Single());
        var browseCommands = item.MoreCommands
            .OfType<CommandContextItem>()
            .Select(context => context.Command)
            .OfType<OpenInBrowserCommand>()
            .Where(command => command.Name is "Open issues" or "Open pull requests")
            .ToArray();

        CollectionAssert.AreEqual(RepositoryBrowserActions, browseCommands.Select(command => command.Name).ToArray());
        var issuesResult = browseCommands[0].Invoke();
        Assert.AreEqual(new Uri("https://github.com/octocat/toolkit/issues"), browser.LastOpened);
        Assert.AreEqual(CommandResultKind.Dismiss, issuesResult.Kind);
        var pullRequestsResult = browseCommands[1].Invoke();
        Assert.AreEqual(new Uri("https://github.com/octocat/toolkit/pulls"), browser.LastOpened);
        Assert.AreEqual(CommandResultKind.Dismiss, pullRequestsResult.Kind);
    }

    [TestMethod]
    public async Task RepositoryMenu_IssueAndPullRequestSectionsOpenTheirCommandPaletteLists()
    {
        var issue = new GitHubIssue(1, "Bug", null, SubjectState.Open, new Uri("https://github.com/octocat/toolkit/issues/1"), DateTimeOffset.UtcNow, "octocat", [], ["bug"], 0);
        var pullRequest = new GitHubPullRequest
        {
            Number = 2,
            Title = "Feature",
            WebUrl = new Uri("https://github.com/octocat/toolkit/pull/2"),
            State = SubjectState.Open,
            HeadBranch = "feature",
            BaseBranch = "main",
        };
        var issuesClient = new Mock<IIssuesClient>();
        issuesClient.Setup(c => c.GetIssuesAsync(Account, "octocat/toolkit", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([issue], null));
        var pullRequestsClient = new Mock<IPullRequestsClient>();
        pullRequestsClient.Setup(c => c.GetPullRequestsAsync(Account, "octocat/toolkit", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult([pullRequest], null));
        var repositoriesClient = Client([RepoFormattingTests.Repo("octocat/toolkit")]);
        using var page = CreatePage(
            repositoriesClient.Object,
            out _,
            issuesClient.Object,
            pullRequestsClient.Object,
            out _,
            out _);
        page.GetItems();
        await page.CurrentLoad;
        var repositoryPage = Assert.IsInstanceOfType<RepositoryPage>(page.GetItems().Single().Command);

        Assert.AreEqual("octocat/toolkit", repositoryPage.Title);
        var sections = repositoryPage.GetItems();
        CollectionAssert.AreEqual(
            RepositorySectionsForToolkit,
            sections.Select(area => area.Title).ToArray());

        var issuesPage = Assert.IsInstanceOfType<RepositoryIssuesPage>(sections.Single(item => item.Title == "Issues").Command);
        issuesPage.GetItems();
        await issuesPage.CurrentLoad;
        Assert.AreEqual("octocat/toolkit issues", issuesPage.Title);
        Assert.AreEqual("#1 Bug", issuesPage.GetItems().Single().Title);
        issuesClient.Verify(c => c.GetIssuesAsync(Account, "octocat/toolkit", null, It.IsAny<CancellationToken>()), Times.Once);

        var pullRequestsPage = Assert.IsInstanceOfType<RepositoryPullRequestsPage>(sections.Single(item => item.Title == "Pull Requests").Command);
        pullRequestsPage.GetItems();
        await pullRequestsPage.CurrentLoad;
        Assert.AreEqual("octocat/toolkit pull requests", pullRequestsPage.Title);
        Assert.AreEqual("#2 Feature", pullRequestsPage.GetItems().Single().Title);
        pullRequestsClient.Verify(c => c.GetPullRequestsAsync(Account, "octocat/toolkit", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("Issues")]
    [DataRow("Pull Requests")]
    [DataRow("Actions")]
    public async Task RepositorySections_KeepTheirOwnListsWhenSwitchingRepositories(string section)
    {
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        var issues = new Mock<IIssuesClient>();
        issues.Setup(c => c.GetIssuesAsync(Account, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([], null));
        var pulls = new Mock<IPullRequestsClient>();
        pulls.Setup(c => c.GetPullRequestsAsync(Account, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestsPageResult([], null));
        var runs = new Mock<IActionsClient>();
        runs.Setup(c => c.GetRunsAsync(Account, It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([], null));
        using var issuesTemplate = new RepositoryIssuesPage(auth, issues.Object, browser);
        using var pullsTemplate = new RepositoryPullRequestsPage(auth, pulls.Object, browser);
        using var actionsTemplate = new ActionsPage(auth, runs.Object, browser);
        using var first = new RepositoryPage(browser, actionsTemplate, RepoFormattingTests.Repo("o/a"), issuesTemplate, pullsTemplate);
        using var second = new RepositoryPage(browser, actionsTemplate, RepoFormattingTests.Repo("o/b"), issuesTemplate, pullsTemplate);
        var firstList = Assert.IsInstanceOfType<DynamicListPage>(first.GetItems().Single(i => i.Title == section).Command);
        var secondList = Assert.IsInstanceOfType<DynamicListPage>(second.GetItems().Single(i => i.Title == section).Command);

        Assert.AreNotSame(firstList, secondList);
        Assert.AreNotEqual(firstList.Id, secondList.Id);
        issues.VerifyNoOtherCalls();
        pulls.VerifyNoOtherCalls();
        runs.VerifyNoOtherCalls();
        firstList.GetItems();
        await Load(firstList);
        firstList.SearchText = "keep my filter";
        secondList.GetItems();
        await Load(secondList);
        firstList.GetItems();

        Assert.AreEqual("keep my filter", firstList.SearchText);
        Assert.AreEqual(string.Empty, secondList.SearchText);
        Assert.StartsWith("o/a ", firstList.Title);
        Assert.StartsWith("o/b ", secondList.Title);
        Assert.IsNull(browser.LastOpened);
        foreach (var repository in new[] { "o/a", "o/b" })
        {
            switch (section)
            {
                case "Issues":
                    issues.Verify(c => c.GetIssuesAsync(Account, repository, null, It.IsAny<CancellationToken>()), Times.Once);
                    break;
                case "Pull Requests":
                    pulls.Verify(c => c.GetPullRequestsAsync(Account, repository, null, It.IsAny<CancellationToken>()), Times.Once);
                    break;
                case "Actions":
                    runs.Verify(c => c.GetRunsAsync(Account, repository, null, It.IsAny<CancellationToken>()), Times.Once);
                    break;
            }
        }

        static Task Load(DynamicListPage page) => page switch
        {
            RepositoryIssuesPage issuesPage => issuesPage.CurrentLoad,
            RepositoryPullRequestsPage pullsPage => pullsPage.CurrentLoad,
            ActionsPage actionsPage => actionsPage.CurrentLoad,
            _ => throw new InvalidOperationException("Unexpected repository section"),
        };
    }

    [TestMethod]
    [DataRow("Issues", "issues")]
    [DataRow("Pull Requests", "pulls")]
    [DataRow("Discussions", "discussions")]
    [DataRow("Actions", "actions")]
    public void RepositoryMenu_OpensSectionOnRepositoryHost(string section, string path)
    {
        var browser = new FakeBrowser(_ => null);
        var repository = RepoFormattingTests.Repo("o/a") with { WebUrl = new Uri("https://github.example.com/o/a/") };
        var page = new RepositoryPage(browser, null, repository);

        ((InvokableCommand)page.GetItems().Single(i => i.Title == section).Command!).Invoke();

        Assert.AreEqual(new Uri($"https://github.example.com/o/a/{path}"), browser.LastOpened);
    }

    [TestMethod]
    public void RepositoryPages_KeepTheirOwnRepositoryContext()
    {
        var browser = new FakeBrowser(_ => null);
        var first = new RepositoryPage(browser, null, RepoFormattingTests.Repo("o/a"));
        first.SearchText = "issues";
        var second = new RepositoryPage(browser, null, RepoFormattingTests.Repo("o/b", description: "Another repo"));

        Assert.AreNotEqual(first.Id, second.Id);
        Assert.AreEqual("issues", first.SearchText);
        Assert.AreEqual("o/a", first.Title);
        var overview = second.GetItems()[0];
        Assert.AreEqual("o/b", overview.Title);
        Assert.AreEqual("Another repo", overview.Subtitle);
        ((InvokableCommand)overview.Command!).Invoke();
        Assert.AreEqual(new Uri("https://github.com/o/b"), browser.LastOpened);
        foreach (var item in second.GetItems())
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
        client.Setup(c => c.SearchAsync(Account, "remote", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/remote")], null, 1));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "remote";
        await page.CurrentSearch;

        var repository = Assert.IsInstanceOfType<RepositoryPage>(page.GetItems().Single().Command);

        Assert.AreEqual("o/remote", repository.Title);
        Assert.AreEqual("o/remote", repository.GetItems()[0].Title);
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

    [TestMethod]
    public async Task Search_LoadsExplicitPagesAndDeduplicatesLocalAndRemoteRows()
    {
        var next = new Uri("https://api.github.com/search/repositories?q=power&per_page=30&page=2");
        var client = Client([RepoFormattingTests.Repo("o/power")]);
        client.Setup(c => c.SearchAsync(Account, "power", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/power"), RepoFormattingTests.Repo("o/first")], next, 3));
        client.Setup(c => c.SearchAsync(Account, "power", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/first"), RepoFormattingTests.Repo("o/second")], null, 3));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "power";
        await page.CurrentSearch;

        Assert.HasCount(2, page.GetItems().OfType<RepoItem>());
        Assert.Contains("Showing 2 of 3", page.GetItems()[2].Subtitle);
        Assert.AreEqual("Load more", page.GetItems().Last().Title);
        Assert.IsFalse(page.HasMoreItems);
        client.Verify(c => c.SearchAsync(Account, "power", next, It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsInstanceOfType<InvokableCommand>(page.GetItems().Last().Command).Invoke();
        await page.CurrentSearch;

        Assert.HasCount(3, page.GetItems());
        Assert.AreEqual("o/second", page.GetItems().Last().Title);
        Assert.AreEqual("power", page.SearchText);
        client.Verify(c => c.SearchAsync(Account, "power", next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Search_EmptyPageStillOffersExplicitContinuation()
    {
        var next = new Uri("https://api.github.com/search/repositories?q=missing&per_page=30&page=2");
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "missing", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([], next, 1));
        client.Setup(c => c.SearchAsync(Account, "missing", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/found")], null, 1));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "missing";
        await page.CurrentSearch;

        Assert.IsEmpty(page.GetItems().OfType<RepoItem>());
        Assert.Contains("Showing 0 of 1", page.GetItems().First().Subtitle);
        Assert.AreEqual("Load more", page.GetItems().Last().Title);
        var more = page.GetItems().Last();
        Assert.AreSame(more, page.GetItems().Last());
        Assert.IsInstanceOfType<InvokableCommand>(more.Command).Invoke();
        await page.CurrentSearch;
        Assert.AreEqual("o/found", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task Search_PaginationFailureRetainsResultsAndRetriesContinuation()
    {
        var next = new Uri("https://api.github.com/search/repositories?q=repo&per_page=30&page=2");
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "repo", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/first")], next, 2));
        client.SetupSequence(c => c.SearchAsync(Account, "repo", next, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/second")], null, 2));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "repo";
        await page.CurrentSearch;
        var first = page.GetItems().First();
        page.LoadMore();
        await page.CurrentSearch;

        Assert.AreSame(first, page.GetItems().First());
        Assert.AreEqual("rate limited", page.GetItems()[1].Subtitle);
        Assert.AreEqual("Retry loading more", page.GetItems().Last().Title);
        Assert.IsFalse(page.IsLoading);
        Assert.IsInstanceOfType<InvokableCommand>(page.GetItems().Last().Command).Invoke();
        await page.CurrentSearch;
        Assert.HasCount(2, page.GetItems());
        client.Verify(c => c.SearchAsync(Account, "repo", next, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Search_QueryChangeRejectsLatePaginationResponseAndResetsContinuation(bool failOldPage)
    {
        var next = new Uri("https://api.github.com/search/repositories?q=old&per_page=30&page=2");
        var newNext = new Uri("https://api.github.com/search/repositories?q=new&per_page=30&page=2");
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<RepositorySearchPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "old", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/old")], next, 2));
        client.Setup(c => c.SearchAsync(Account, "old", next, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, Uri? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return pending.Task;
            });
        client.Setup(c => c.SearchAsync(Account, "new", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/new")], newNext, 2));
        client.Setup(c => c.SearchAsync(Account, "new", newNext, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/new-second")], null, 2));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "old";
        await page.CurrentSearch;
        page.LoadMore();
        var oldPage = page.CurrentSearch;
        var token = await started.Task;
        page.LoadMore();
        client.Verify(c => c.SearchAsync(Account, "old", next, It.IsAny<CancellationToken>()), Times.Once);
        page.SearchText = "new";
        await page.CurrentSearch;
        Assert.IsTrue(token.IsCancellationRequested);
        if (failOldPage)
        {
            pending.SetException(new GitHubApiException("stale error"));
        }
        else
        {
            pending.SetResult(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/stale")], null, 9000));
        }
        await oldPage;

        Assert.AreEqual("o/new", page.GetItems().OfType<RepoItem>().Single().Title);
        Assert.DoesNotContain("stale", string.Join(" ", page.GetItems().Select(item => item.Subtitle)));
        Assert.IsFalse(page.IsLoading);
        page.LoadMore();
        await page.CurrentSearch;
        Assert.HasCount(2, page.GetItems());
        client.Verify(c => c.SearchAsync(Account, "new", newNext, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Search_FirstPageErrorRemainsVisibleWithLocalMatches()
    {
        var client = Client([RepoFormattingTests.Repo("o/repo")]);
        client.Setup(c => c.SearchAsync(Account, "repo", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "repo";
        await page.CurrentSearch;
        Assert.HasCount(1, page.GetItems().OfType<RepoItem>());
        Assert.AreEqual("Couldn't search GitHub", page.GetItems().Last().Title);
        Assert.AreEqual("rate limited", page.GetItems().Last().Subtitle);
        Assert.IsInstanceOfType<RefreshReposCommand>(page.GetItems().Last().Command);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Search_ResultLimitIsVisibleBeforeAndAfterLastAccessiblePage(bool hasMore)
    {
        var client = Client([]);
        client.Setup(c => c.SearchAsync(Account, "repo", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/repo")],
                hasMore ? new Uri("https://api.github.com/search/repositories?q=repo&per_page=30&page=2") : null, 1001));
        using var page = await LoadedPage(client.Object);
        page.SearchText = "repo";
        await page.CurrentSearch;
        Assert.Contains("1,000", page.GetItems()[1].Title);
        Assert.Contains("Narrow your search", page.GetItems()[1].Subtitle);
        Assert.AreEqual(hasMore, page.GetItems().Any(item => item.Command is LoadMoreResultsCommand));
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    [DataRow("clear")]
    [DataRow("sign-out")]
    [DataRow("dispose")]
    [DataRow("refresh")]
    public async Task Search_ScopeResetCancelsPaginationWithoutBlockingHostReads(string action)
    {
        var next = new Uri("https://api.github.com/search/repositories?q=repo&per_page=30&page=2");
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<RepositorySearchPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([RepoFormattingTests.Repo("o/local")]);
        client.SetupSequence(c => c.SearchAsync(Account, "repo", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/first")], next, 2))
            .ReturnsAsync(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/refreshed")], null, 1));
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var page = new ReposPage(auth, client.Object, browser, issues, pulls, searchDelay: TimeSpan.Zero);
        var blocked = false;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.SearchAsync(Account, "repo", next, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, Uri? _, CancellationToken token) =>
            {
                using var registration = token.Register(() =>
                {
                    var read = Task.Factory.StartNew(() =>
                    {
                        _ = page.CurrentSearch;
                        page.GetItems();
                    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    blocked = !read.Wait(TimeSpan.FromSeconds(2));
                    cancelled.SetResult();
                });
                started.SetResult(token);
                return await pending.Task;
            });
        page.GetItems();
        await page.CurrentLoad;
        page.SearchText = "repo";
        await page.CurrentSearch;
        page.LoadMore();
        var oldPage = page.CurrentSearch;
        var token = await started.Task;

        switch (action)
        {
            case "clear":
                page.SearchText = string.Empty;
                break;
            case "sign-out":
                auth.SignOut();
                break;
            case "dispose":
                page.Dispose();
                break;
            case "refresh":
                await page.RefreshAsync();
                await page.CurrentSearch;
                break;
        }
        Assert.IsTrue(token.IsCancellationRequested);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending.SetResult(new RepositorySearchPageResult([RepoFormattingTests.Repo("o/stale")], null, 2));
        await oldPage;

        Assert.IsFalse(blocked, "Cancellation callbacks must be able to read the page from another thread.");
        Assert.IsFalse(page.IsLoading);
        Assert.IsFalse(page.GetItems().Any(item => item.Title == "o/stale"));
        if (action == "clear")
        {
            Assert.AreEqual("o/local", page.GetItems().Single().Title);
        }
        else if (action == "sign-out")
        {
            Assert.IsEmpty(page.GetItems());
        }
        else if (action == "refresh")
        {
            Assert.AreEqual("o/refreshed", page.GetItems().Single().Title);
        }
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
        => CreatePage(
            client,
            out browser,
            Mock.Of<IIssuesClient>(),
            Mock.Of<IPullRequestsClient>(),
            out _,
            out _);

    private static ReposPage CreatePage(
        IRepositoriesClient client,
        out FakeBrowser browser,
        IIssuesClient issuesClient,
        IPullRequestsClient pullRequestsClient,
        out RepositoryIssuesPage issuesPage,
        out RepositoryPullRequestsPage pullRequestsPage)
    {
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.GetUtcNow()).Returns(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));
        issuesPage = new RepositoryIssuesPage(auth, issuesClient, browser, time.Object);
        pullRequestsPage = new RepositoryPullRequestsPage(auth, pullRequestsClient, browser, time.Object);
        var page = new ReposPage(auth, client, browser, issuesPage, pullRequestsPage, time.Object, TimeSpan.Zero);
        return page;
    }

}
