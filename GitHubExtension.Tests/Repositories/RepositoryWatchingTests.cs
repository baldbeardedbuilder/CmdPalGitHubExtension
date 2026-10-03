using System.Net;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public sealed class RepositoryWatchingTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly string[] WatchingRoutes = ["/repos/o/r/subscription", "/repos/o/r"];
    public TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task Watching_ReadsActualSubscriptionNotPublicCount(bool subscribed, bool ignored)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("/repos/o/r/subscription", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent($$"""{"subscribed":{{subscribed.ToString().ToLowerInvariant()}},"ignored":{{ignored.ToString().ToLowerInvariant()}}}""") };
        }));
        var state = await new RepositoriesClient(http).GetWatchingAsync(Account, "o/r", TestContext.CancellationToken);
        Assert.AreEqual(new RepositorySubscription(subscribed, ignored), state);
    }

    [TestMethod]
    public async Task MissingSubscription_VerifiesRepositoryAccess()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(request =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return new(calls.Count == 1 ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        }));
        var state = await new RepositoriesClient(http).GetWatchingAsync(Account, "o/r", TestContext.CancellationToken);
        CollectionAssert.AreEqual(WatchingRoutes, calls);
        Assert.AreEqual(new RepositorySubscription(false, false), state);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    public async Task DeniedWatching_DoesNotReportNotWatching(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new RepositoriesClient(http).GetWatchingAsync(Account, "o/r", TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(0, "PUT", """{"subscribed":true,"ignored":false}""")]
    [DataRow(2, "PUT", """{"subscribed":false,"ignored":true}""")]
    [DataRow(1, "DELETE", null)]
    public async Task WatchingMutation_UsesExplicitBody(int action, string method, string? body)
    {
        using var http = new HttpClient(new AsyncHandler(async request =>
        {
            Assert.AreEqual(method, request.Method.Method);
            Assert.AreEqual(body, request.Content is null ? null : await request.Content.ReadAsStringAsync(TestContext.CancellationToken));
            return new(HttpStatusCode.NoContent);
        }));
        await new RepositoriesClient(http).SetWatchingAsync(Account, "o/r", (RepositoryWatchAction)action, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task StateRace_DoesNotWriteWhenSubscriptionChangedAfterReview()
    {
        var client = new Mock<IRepositoryWatchingClient>();
        client.Setup(c => c.GetWatchingAsync(Account, "o/r", It.IsAny<CancellationToken>())).ReturnsAsync(new RepositorySubscription(false, true));
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = new RepositoryWatchPage(client.Object, executor, Account, "o/r", () => true, new FakeBrowser(_ => null));
        await page.SetAsync(new(true, false), RepositoryWatchAction.Unwatch, TestContext.CancellationToken);
        client.Verify(c => c.SetWatchingAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<RepositoryWatchAction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task SessionInvalidationDuringValidation_DoesNotSubmitOrPublish()
    {
        var current = true;
        var client = new Mock<IRepositoryWatchingClient>(MockBehavior.Strict);
        client.Setup(c => c.GetWatchingAsync(Account, "o/r", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                current = false;
                return Task.FromResult(new RepositorySubscription(false, false));
            });
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = new RepositoryWatchPage(client.Object, executor, Account, "o/r", () => current, new FakeBrowser(_ => null));

        await page.SetAsync(new(false, false), RepositoryWatchAction.Watch, TestContext.CancellationToken);

        Assert.IsEmpty(page.GetItems());
        client.Verify(c => c.GetWatchingAsync(Account, "o/r", It.IsAny<CancellationToken>()), Times.Once);
        client.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task AccountSwitch_InvalidatesWatchingMutation()
    {
        var client = new Mock<IRepositoryWatchingClient>(MockBehavior.Strict);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = new RepositoryWatchPage(client.Object, executor, Account, "o/r", () => true, new FakeBrowser(_ => null));
        auth.SignOut();
        await page.SetAsync(new(false, false), RepositoryWatchAction.Watch, TestContext.CancellationToken);
        Assert.IsEmpty(page.GetItems());
        client.VerifyNoOtherCalls();
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request);
    }
}
