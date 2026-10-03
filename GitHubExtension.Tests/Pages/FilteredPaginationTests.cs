// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public sealed class FilteredPaginationTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly Uri Next = new("https://api.github.com/items?page=2");
    private static readonly Uri Third = new("https://api.github.com/items?page=3");

    [TestMethod]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task LocalFilters_ReachLaterMatchesOnePageAtATime(string kind)
    {
        var requests = new List<Uri?>();
        var (page, load) = CreatePage(kind, (next, _) =>
        {
            requests.Add(next);
            return Task.FromResult(next is null
                ? new Result([new Row(1, "Closed earlier", SubjectState.Closed)], Next)
                : next == Next ? new Result([], Third)
                : new Result([new Row(2, "Keyboard fixed", SubjectState.Open)], null));
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        await load();

        Assert.IsFalse(page.HasMoreItems);
        Assert.IsEmpty(Rows(page));
        Assert.Contains("loaded", page.EmptyContent!.Title);
        Assert.DoesNotContain("doesn't have any", page.EmptyContent.Subtitle);
        page.SearchText = "keyboard";
        Assert.HasCount(1, requests);
        Assert.IsEmpty(Rows(page));
        var more = page.GetItems().Last();
        Assert.AreEqual("Load more", more.Title);
        Assert.AreSame(more, page.GetItems().Last());
        Assert.Contains("1 loaded", page.GetItems().First().Subtitle);

        Assert.IsInstanceOfType<InvokableCommand>(more.Command).Invoke();
        await load();
        Assert.HasCount(2, requests);
        Assert.IsEmpty(Rows(page));
        Assert.AreEqual("Load more", page.GetItems().Last().Title);
        Assert.IsFalse(page.HasMoreItems);

        Assert.IsInstanceOfType<InvokableCommand>(page.GetItems().Last().Command).Invoke();
        await load();
        Assert.HasCount(3, requests);
        Assert.AreEqual("#2 Keyboard fixed", Rows(page).Single().Title);
        Assert.AreEqual("keyboard", page.SearchText);
        Assert.AreEqual("open", page.Filters!.CurrentFilterId);
        Assert.HasCount(1, page.GetItems());
        Assert.AreEqual(Next, requests[1]);
        Assert.AreEqual(Third, requests[2]);
    }

    [TestMethod]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task LocalFilters_ReuseLoadedPagesAndKeepScopeVisibleWithMatches(string kind)
    {
        var calls = 0;
        var (page, load) = CreatePage(kind, (_, _) =>
        {
            calls++;
            return Task.FromResult(new Result(
                [new Row(1, "Open keyboard", SubjectState.Open), new Row(2, "Closed keyboard", SubjectState.Closed)], Next));
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        await load();
        page.SearchText = "keyboard";
        Assert.AreEqual("#1 Open keyboard", Rows(page).Single().Title);
        Assert.Contains("loaded", page.GetItems()[1].Title);
        page.Filters!.CurrentFilterId = "closed";
        Assert.AreEqual("#2 Closed keyboard", Rows(page).Single().Title);
        Assert.Contains("closed", page.GetItems()[1].Subtitle);
        page.SearchText = "missing";
        Assert.IsEmpty(Rows(page));
        Assert.Contains("loaded", page.EmptyContent!.Title);
        Assert.AreEqual("Load more", page.GetItems().Last().Title);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task PaginationFailure_PreservesRowsAndRetriesSamePage(string kind)
    {
        var fail = true;
        var requests = new List<Uri?>();
        var (page, load) = CreatePage(kind, (next, _) =>
        {
            requests.Add(next);
            if (next is not null && fail)
            {
                throw new GitHubApiException("rate limited");
            }

            return Task.FromResult(new Result([new Row(next is null ? 1 : 2, "Keyboard", SubjectState.Open)],
                next is null ? Next : null));
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        await load();
        page.SearchText = "keyboard";
        var first = Rows(page).Single();
        Assert.IsInstanceOfType<InvokableCommand>(page.GetItems().Last().Command).Invoke();
        await load();

        Assert.AreSame(first, Rows(page).Single());
        Assert.Contains("rate limited", page.GetItems()[1].Subtitle);
        Assert.AreEqual("Retry loading more", page.GetItems().Last().Title);
        Assert.IsFalse(page.IsLoading);
        fail = false;
        Assert.IsInstanceOfType<InvokableCommand>(page.GetItems().Last().Command).Invoke();
        await load();

        Assert.HasCount(2, Rows(page));
        Assert.HasCount(2, page.GetItems());
        Assert.AreEqual(Next, requests[1]);
        Assert.AreEqual(Next, requests[2]);
    }

    [TestMethod]
    [DataRow("issues")]
    [DataRow("pull-requests")]
    public async Task Loading_DoesNotClaimCompleteEmptyResultsOrStartExtraRequests(string kind)
    {
        var pending = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var (page, load) = CreatePage(kind, (_, _) =>
        {
            calls++;
            started.SetResult();
            return pending.Task;
        });
        using var lifetime = (IDisposable)page;
        page.GetItems();
        await started.Task;
        page.SearchText = "missing";
        Assert.Contains("Loading", page.EmptyContent!.Title);
        page.GetItems();
        Assert.Contains("Loading", page.EmptyContent.Title);
        page.LoadMore();
        Assert.AreEqual(1, calls);
        pending.SetResult(new Result([], null));
        await load();
        Assert.IsEmpty(page.GetItems());
        Assert.DoesNotContain("loaded", page.EmptyContent.Title);
        Assert.AreEqual("Nothing matches \"missing\"", page.EmptyContent.Subtitle);
    }

    private static IListItem[] Rows(DynamicListPage page) =>
        [.. page.GetItems().Where(item => item is RepositoryIssueItem or RepositoryPullRequestItem)];

    private static (DynamicListPage Page, Func<Task> Load) CreatePage(
        string kind, Func<Uri?, CancellationToken, Task<Result>> fetch)
    {
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        if (kind == "issues")
        {
            var client = new Mock<IIssuesClient>();
            client.Setup(c => c.GetIssuesAsync(Account, "o/r", It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
                .Returns(async (GitHubAccount _, string _, Uri? next, CancellationToken token) =>
                {
                    var result = await fetch(next, token);
                    return new IssuesPageResult([.. result.Rows.Select(row => new GitHubIssue(row.Number, row.Title, null,
                        row.State, new Uri($"https://github.com/o/r/issues/{row.Number}"), DateTimeOffset.UnixEpoch, null, [], [], 0))],
                        result.Next);
                });
            var page = new RepositoryIssuesPage(auth, client.Object, browser);
            page.Open("o/r");
            return (page, () => page.CurrentLoad);
        }

        var pulls = new Mock<IPullRequestsClient>();
        pulls.Setup(c => c.GetPullRequestsAsync(Account, "o/r", It.IsAny<Uri?>(), It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, Uri? next, CancellationToken token) =>
            {
                var result = await fetch(next, token);
                return new PullRequestsPageResult([.. result.Rows.Select(row => new GitHubPullRequest
                {
                    Number = row.Number,
                    Title = row.Title,
                    State = row.State,
                    WebUrl = new Uri($"https://github.com/o/r/pull/{row.Number}"),
                })], result.Next);
            });
        var pullsPage = new RepositoryPullRequestsPage(auth, pulls.Object, browser);
        pullsPage.Open("o/r");
        return (pullsPage, () => pullsPage.CurrentLoad);
    }

    private sealed record Row(int Number, string Title, SubjectState State);
    private sealed record Result(IReadOnlyList<Row> Rows, Uri? Next);
}
