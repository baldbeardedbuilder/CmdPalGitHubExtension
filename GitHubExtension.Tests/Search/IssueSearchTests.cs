using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Search;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Search;

[TestClass]
public sealed class IssueSearchTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "never-write-this-token");
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Search_EncodesQualifiersAndDistinguishesIssuesFromPullRequests()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            Assert.AreEqual("""is:open label:"needs review" org:test""", query["q"]);
            return Json("""
            {"total_count":2,"incomplete_results":false,"items":[
                { "id":1,"number":7,"title":"Issue","html_url":"https://github.com/o/r/issues/7","repository_url":"https://api.github.com/repos/o/r","state":"open"},
                {"id":2,"number":8,"title":"PR","html_url":"https://github.com/o/r/pull/8","repository_url":"https://api.github.com/repos/o/r","state":"open","pull_request":{}}]}
            """);
        }));
        var result = await new IssueSearchClient(http).SearchAsync(Account, """is:open label:"needs review" org:test""", IssueSearchKind.All, null, TestContext.CancellationToken);
        Assert.IsFalse(result.Items[0].IsPullRequest);
        Assert.IsTrue(result.Items[1].IsPullRequest);
        Assert.AreEqual("o/r", result.Items[0].Repository);
    }

    [TestMethod]
    public void SearchScope_RejectsConflictingQualifiers()
    {
        Assert.AreEqual("assignee:@me is:issue", IssueSearchClient.ScopeQuery("assignee:@me", IssueSearchKind.Issues));
        Assert.AreEqual("review-requested:@me is:pr", IssueSearchClient.ScopeQuery("review-requested:@me", IssueSearchKind.PullRequests));
        Assert.ThrowsExactly<GitHubApiException>(() => IssueSearchClient.ScopeQuery("is:pr", IssueSearchKind.Issues));
    }

    [TestMethod]
    public async Task SearchResultLimit_TrimsFinalPageAndStopsAtOneThousand()
    {
        var items = string.Join(',', Enumerable.Range(991, 30).Select(n =>
            $$"""{"id":{{n}},"number":{{n}},"title":"Result","html_url":"https://github.com/o/r/issues/{{n}}","repository_url":"https://api.github.com/repos/o/r"}"""));
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = Json($$"""{"total_count":1200,"incomplete_results":true,"items":[{{items}}]}""");
            response.Headers.Add("Link", "<https://api.github.com/search/issues?q=test&per_page=30&page=35>; rel=\"next\"");
            return response;
        }));
        var result = await new IssueSearchClient(http).SearchAsync(Account, "test", IssueSearchKind.All,
            new Uri("https://api.github.com/search/issues?q=test&per_page=30&page=34"), TestContext.CancellationToken);
        Assert.HasCount(10, result.Items);
        Assert.AreEqual(1200, result.Total);
        Assert.IsNull(result.NextPage);
        Assert.IsTrue(result.Incomplete);
    }

    [TestMethod]
    public async Task SearchPagination_RejectsChangedQueryAndUnsafeHost()
    {
        using var http = new HttpClient(new Handler(_ => throw new AssertFailedException("Request should not be sent.")));
        var client = new IssueSearchClient(http);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => client.SearchAsync(Account, "first", IssueSearchKind.All,
            new Uri("https://api.github.com/search/issues?q=second&per_page=30&page=2"), TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => client.SearchAsync(Account, "first", IssueSearchKind.All,
            new Uri("https://evil.example/search/issues?q=first&per_page=30&page=2"), TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    public async Task SearchRateErrors_AreNotEmptyResults(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new IssueSearchClient(http).SearchAsync(Account, "test", IssueSearchKind.All, null, TestContext.CancellationToken));
    }

    [TestMethod]
    public void SavedQueries_PersistPerAccountAndHostWithoutTokens()
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "search-test-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SavedIssueQueryStore(directory);
            store.Save(Account, new("Assigned", "assignee:@me", IssueSearchKind.Issues));
            var reloaded = new SavedIssueQueryStore(directory);
            Assert.AreEqual("assignee:@me", reloaded.Load(Account).Single().Query);
            Assert.IsEmpty(reloaded.Load(Account with { Login = "other" }));
            Assert.IsTrue(GitHubHost.TryParse("git.example.com", out var host));
            Assert.IsEmpty(reloaded.Load(Account with { Host = host! }));
            Assert.HasCount(1, reloaded.Load(Account with { Token = "rotated-token" }));
            var contents = File.ReadAllText(store.AccountPath(Account));
            Assert.DoesNotContain(Account.Token, contents);
            using var json = JsonDocument.Parse(contents);
            Assert.AreEqual("Assigned", json.RootElement[0].GetProperty("Name").GetString());
            store.Save(Account, new("assigned", "is:open", IssueSearchKind.Issues));
            Assert.HasCount(1, store.Load(Account));
            store.Delete(Account, "ASSIGNED");
            Assert.IsEmpty(store.Load(Account));
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [TestMethod]
    public async Task NativeDetails_AreCachedAndInvalidatedWithSearchQuery()
    {
        var item = new IssueSearchResult(1, 7, "Work", new Uri("https://github.com/o/r/pull/7"),
            "o/r", true, "open", null, null);
        var client = new Mock<IIssueSearchClient>();
        client.Setup(c => c.SearchAsync(Account, It.IsAny<string>(), IssueSearchKind.All, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueSearchPageResult([item], null, 1, false));
        using var auth = Auth();
        var created = new List<NativePage>();
        ListPage Create(GitHubAccount account, string repository, int number, Func<bool> current)
        {
            Assert.AreEqual(Account, account);
            Assert.AreEqual("o/r", repository);
            Assert.AreEqual(7, number);
            var native = new NativePage(current);
            created.Add(native);
            return native;
        }
        using var page = new IssueSearchPage(auth, client.Object, new FakeBrowser(_ => null),
            Mock.Of<ISavedIssueQueryStore>(), new(Create, (account, repository, number, pullRequest, current) =>
            {
                Assert.IsTrue(pullRequest);
                return Create(account, repository, number, current);
            }));
        await page.ExecuteQuery("first", IssueSearchKind.All);
        var row = page.GetItems().Single(i => i.Title == "#7 Work");
        page.GetItems();
        Assert.HasCount(2, created);
        Assert.IsTrue(created.All(native => native.IsCurrent()));
        var guard = created[0].IsCurrent;
        var browserCommand = Assert.IsInstanceOfType<InvokableCommand>(row.Command);
        await page.ExecuteQuery("second", IssueSearchKind.All);
        Assert.IsFalse(guard());
        Assert.IsTrue(created.All(native => native.Disposed));
        browserCommand.Invoke();
    }

    [TestMethod]
    public async Task SearchPage_KeepsBrowserNavigationAndClearsSavedScopeOnAccountSwitch()
    {
        var client = new Mock<IIssueSearchClient>();
        var item = new IssueSearchResult(1, 7, "Fix tests", new Uri("https://github.com/o/r/pull/7"), "o/r", true, "open", "octocat", "description");
        client.Setup(c => c.SearchAsync(Account, "review-requested:@me", IssueSearchKind.PullRequests, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueSearchPageResult([item], null, 1, false));
        using var auth = Auth();
        var browser = new FakeBrowser(_ => null);
        using var page = new IssueSearchPage(auth, client.Object, browser, Mock.Of<ISavedIssueQueryStore>(
            s => s.Load(It.IsAny<GitHubAccount>()) == Array.Empty<SavedIssueQuery>()));
        await page.ExecuteQuery("review-requested:@me", IssueSearchKind.PullRequests);
        var row = page.GetItems().Single(i => i.Title == "#7 Fix tests");
        Assert.AreEqual("Pull request", row.Tags.Single().Text);
        Assert.IsInstanceOfType<InvokableCommand>(row.Command).Invoke();
        Assert.AreEqual(item.WebUrl, browser.LastOpened);
        auth.SignOut();
        Assert.IsEmpty(page.GetItems());
        var sentinel = new Uri("https://github.com/sentinel");
        browser.Open(sentinel);
        Assert.IsInstanceOfType<InvokableCommand>(row.Command).Invoke();
        Assert.AreEqual(sentinel, browser.LastOpened);
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }

    private sealed partial class NativePage(Func<bool> current) : ListPage, IDisposable
    {
        internal Func<bool> IsCurrent => current;
        internal bool Disposed { get; private set; }
        public override IListItem[] GetItems() => [];
        public void Dispose() => Disposed = true;
    }
}
