using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Notifications;

[TestClass]
public sealed class NotificationBrowsingTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Query_EncodesApiFiltersAndConditionalHeader()
    {
        var query = new NotificationQuery(true, true, "o/r", Now.AddDays(-1), Now);
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("/repos/o/r/notifications", request.RequestUri!.AbsolutePath);
            var values = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.AreEqual("false", values["all"]);
            Assert.AreEqual("true", values["participating"]);
            Assert.AreEqual(Now.ToString("O"), values["before"]);
            Assert.AreEqual(Now, request.Headers.IfModifiedSince);
            Assert.AreEqual(Account.Token, request.Headers.Authorization!.Parameter);
            var response = Json("[]");
            response.Content.Headers.LastModified = Now;
            response.Headers.Add("X-Poll-Interval", "120");
            return response;
        }));

        var result = await new NotificationsClient(http).GetNotificationsAsync(Account, query, null, Now, TestContext.CancellationToken);
        Assert.AreEqual(TimeSpan.FromSeconds(120), result.PollInterval);
        Assert.AreEqual(Now, result.LastModified);
        Assert.IsFalse(result.NotModified);
    }

    [TestMethod]
    public async Task ChangedResponseWithoutLastModified_DropsPreviousValidator()
    {
        using var http = new HttpClient(new Handler(_ => Json("[]")));
        var result = await new NotificationsClient(http).GetNotificationsAsync(Account, new(), null, Now,
            TestContext.CancellationToken);
        Assert.IsFalse(result.NotModified);
        Assert.IsNull(result.LastModified);
    }

    [TestMethod]
    public async Task ConditionalNotModified_DoesNotRequireJson()
    {
        using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.NotModified)));
        var result = await new NotificationsClient(http).GetNotificationsAsync(Account, new(), null, Now, TestContext.CancellationToken);
        Assert.IsTrue(result.NotModified);
        Assert.IsEmpty(result.Notifications);
        Assert.AreEqual(TimeSpan.FromSeconds(60), result.PollInterval);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    [DataRow("invalid")]
    public async Task InvalidPollingInterval_UsesSafeDefault(string value)
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = Json("[]");
            response.Headers.Add("X-Poll-Interval", value);
            return response;
        }));
        var result = await new NotificationsClient(http).GetNotificationsAsync(Account, new(), null, null, TestContext.CancellationToken);
        Assert.AreEqual(TimeSpan.FromSeconds(60), result.PollInterval);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    public async Task NotificationErrors_ExplainSupportedTokenTypes(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetNotificationsAsync(Account, new(), null, null, TestContext.CancellationToken));
        Assert.Contains("classic PAT", error.Message);
    }

    [TestMethod]
    public async Task QueryPagination_RejectsDifferentScopeBeforeSending()
    {
        var sent = 0;
        using var http = new HttpClient(new Handler(_ => { sent++; return Json("[]"); }));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new NotificationsClient(http).GetNotificationsAsync(
            Account, new(Participating: true), new Uri("https://api.github.com/notifications?all=true&participating=false&per_page=50&page=2"),
            null, TestContext.CancellationToken));
        Assert.AreEqual(0, sent);
    }

    [TestMethod]
    public async Task PagePolling_RespectsIntervalAndPreservesRowsAndPaginationOn304()
    {
        var mock = new Mock<INotificationsClient>();
        var browsing = mock.As<INotificationBrowsingClient>();
        var next = new Uri("https://api.github.com/notifications?all=true&participating=false&per_page=50&page=2");
        browsing.SetupSequence(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPollResult([NotificationParsingTests.Notification("1")], next, false, Now, TimeSpan.FromSeconds(60)))
            .ReturnsAsync(new NotificationPollResult([], null, true, Now, TimeSpan.FromSeconds(60)));
        using var auth = Auth();
        var clock = new Clock(Now);
        using var page = new NotificationsPage(auth, mock.Object, new FakeBrowser(_ => null), clock);
        page.GetItems();
        await page.CurrentLoad;
        var before = page.GetItems().Single();

        await page.RefreshAsync();
        browsing.Verify(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Once);
        clock.Now = Now.AddSeconds(61);
        await page.RefreshAsync();

        Assert.AreSame(before, page.GetItems().Single());
        Assert.IsTrue(page.HasMoreItems);
        browsing.Verify(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null, Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingPreview_RefreshRetriesWithoutBypassingPolling(bool intervalElapsed)
    {
        var mock = new Mock<INotificationsClient>();
        var browsing = mock.As<INotificationBrowsingClient>();
        var api = new Uri("https://api.github.com/repos/o/r/pulls/1");
        var notification = new GitHubNotification("1", "Preview", "PullRequest", api, "o/r",
            new Uri("https://github.com/o/r"), "review_requested", true, Now);
        browsing.SetupSequence(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null,
                It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPollResult([notification], null, false, Now, TimeSpan.FromSeconds(60)))
            .ReturnsAsync(new NotificationPollResult([], null, true, Now, TimeSpan.FromSeconds(60)));
        mock.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubjectDetails?)null);
        using var auth = Auth();
        var clock = new Clock(Now);
        using var page = new NotificationsPage(auth, mock.Object, new FakeBrowser(_ => null), clock);
        page.GetItems();
        await page.CurrentLoad;
        var row = page.GetItems().Single();
        if (intervalElapsed) { clock.Now = Now.AddSeconds(61); }

        await page.RefreshAsync();

        Assert.AreSame(row, page.GetItems().Single());
        mock.Verify(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()), Times.Exactly(2));
        browsing.Verify(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null,
            It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Exactly(intervalElapsed ? 2 : 1));
    }

    [TestMethod]
    public async Task NativeNotificationDetails_QueryChangeInvalidatesCapturedPagesAndBulkConfirmation()
    {
        var mock = new Mock<INotificationsClient>();
        var browsing = mock.As<INotificationBrowsingClient>();
        var notification = new GitHubNotification("1", "Preview", "PullRequest",
            new Uri("https://api.github.com/repos/o/r/pulls/7"), "o/r", new Uri("https://github.com/o/r"),
            "review_requested", true, Now);
        browsing.Setup(c => c.GetNotificationsAsync(Account, It.IsAny<NotificationQuery>(), null,
                It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPollResult([notification], null, false, Now, TimeSpan.FromSeconds(60)));
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
        using var auth = Auth();
        using var page = new NotificationsPage(auth, mock.Object, new FakeBrowser(_ => null), new Clock(Now),
            workItemDetailFactories: new(Create, (account, repository, number, pullRequest, current) =>
            {
                Assert.IsTrue(pullRequest);
                return Create(account, repository, number, current);
            }));
        page.GetItems();
        await page.CurrentLoad;
        Assert.HasCount(2, created);
        var original = created.ToArray();
        using var bulk = page.BulkReadPage();
        var confirmation = bulk.Confirmation(null);

        await page.SetQuery(new(UnreadOnly: true));

        Assert.IsTrue(original.All(native => native.Disposed && !native.IsCurrent()));
        Assert.IsInstanceOfType<FormContent>(confirmation.GetContent().Single()).SubmitForm("{}", """{"action":"confirm"}""");
        await confirmation.CurrentSubmission;
        browsing.Verify(c => c.MarkAllReadAsync(It.IsAny<GitHubAccount>(), It.IsAny<string?>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task QueryChanges_ResetPaginationAndConditionalValidator()
    {
        var mock = new Mock<INotificationsClient>();
        var browsing = mock.As<INotificationBrowsingClient>();
        browsing.Setup(c => c.GetNotificationsAsync(Account, It.IsAny<NotificationQuery>(), null, It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPollResult([NotificationParsingTests.Notification("1")], null, false, Now, TimeSpan.FromMinutes(5)));
        using var auth = Auth();
        using var page = new NotificationsPage(auth, mock.Object, new FakeBrowser(_ => null), new Clock(Now));
        page.GetItems();
        await page.CurrentLoad;
        var query = new NotificationQuery(UnreadOnly: true, Repository: "o/r");
        await page.SetQuery(query);
        browsing.Verify(c => c.GetNotificationsAsync(Account, query, null, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.HasCount(1, page.GetItems());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    [DataRow(null, "/notifications")]
    [DataRow("o/r", "/repos/o/r/notifications")]
    public async Task BulkRead_SendsExplicitTimestampAndExactScope(string? repository, string path)
    {
        string? body = null;
        using var http = new HttpClient(new AsyncHandler(async request =>
        {
            Assert.AreEqual(HttpMethod.Put, request.Method);
            Assert.AreEqual(path, request.RequestUri!.AbsolutePath);
            body = await request.Content!.ReadAsStringAsync(TestContext.CancellationToken);
            return new(HttpStatusCode.Accepted);
        }));
        var pending = await new NotificationsClient(http).MarkAllReadAsync(Account, repository, Now, TestContext.CancellationToken);
        using var json = JsonDocument.Parse(body!);
        Assert.AreEqual(Now.ToString("O"), json.RootElement.GetProperty("last_read_at").GetString());
        Assert.IsTrue(json.RootElement.GetProperty("read").GetBoolean());
        Assert.IsTrue(pending);
    }

    [TestMethod]
    public async Task BulkConfirmation_CancellationAndAccountSwitchDoNotSend()
    {
        var client = new Mock<INotificationBrowsingClient>(MockBehavior.Strict);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = new BulkNotificationReadPage(Account, client.Object, executor, Now, ["o/r"],
            () => auth.CurrentAccount == Account, () => Task.CompletedTask);
        var confirmation = page.Confirmation(null);
        var form = Assert.IsInstanceOfType<FormContent>(confirmation.GetContent().Single());
        using var card = JsonDocument.Parse(form.TemplateJson);
        Assert.Contains("entire scope", card.RootElement.ToString());
        Assert.Contains(Now.ToString("O"), card.RootElement.GetProperty("body")[2].GetProperty("text").GetString()!);
        form.SubmitForm("{}", """{"action":"cancel"}""");
        await confirmation.CurrentSubmission;
        var stale = page.Confirmation("o/r");
        auth.SignOut();
        Assert.IsInstanceOfType<FormContent>(stale.GetContent().Single()).SubmitForm("{}", """{"action":"confirm"}""");
        await stale.CurrentSubmission;
        client.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task BulkAsyncResponse_ShowsPendingAndRefreshes()
    {
        var client = new Mock<INotificationBrowsingClient>();
        client.Setup(c => c.MarkAllReadAsync(Account, "o/r", Now, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        var refreshes = 0;
        using var page = new BulkNotificationReadPage(Account, client.Object, executor, Now, ["o/r"], () => true,
            () => { refreshes++; return Task.CompletedTask; });
        var confirmation = page.Confirmation("o/r");
        Assert.IsInstanceOfType<FormContent>(confirmation.GetContent().Single()).SubmitForm("{}", """{"action":"confirm"}""");
        await confirmation.CurrentSubmission;
        Assert.AreEqual(1, refreshes);
        Assert.Contains("processing", Assert.IsInstanceOfType<FormContent>(confirmation.GetContent().Single()).TemplateJson);
        client.Verify(c => c.MarkAllReadAsync(Account, "o/r", Now, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PollFailure_RespectsServerIntervalBeforeRetry()
    {
        var mock = new Mock<INotificationsClient>();
        var browsing = mock.As<INotificationBrowsingClient>();
        var error = new GitHubApiException("Rate limited");
        error.Data["NotificationPollInterval"] = TimeSpan.FromSeconds(180);
        browsing.Setup(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(error);
        using var auth = Auth();
        using var page = new NotificationsPage(auth, mock.Object, new FakeBrowser(_ => null), new Clock(Now));
        page.GetItems();
        await page.CurrentLoad;
        await page.RefreshAsync();
        browsing.Verify(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Rate limited", page.EmptyContent!.Subtitle);
    }

    [TestMethod]
    public async Task AccountSwitch_DiscardsLateConditionalResponse()
    {
        var completion = new TaskCompletionSource<NotificationPollResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mock = new Mock<INotificationsClient>();
        mock.As<INotificationBrowsingClient>()
            .Setup(c => c.GetNotificationsAsync(Account, new NotificationQuery(), null, null, It.IsAny<CancellationToken>()))
            .Returns(() => { started.TrySetResult(); return completion.Task; });
        using var auth = Auth();
        using var page = new NotificationsPage(auth, mock.Object, new FakeBrowser(_ => null), new Clock(Now));
        page.GetItems();
        var load = page.CurrentLoad;
        await started.Task.WaitAsync(TestContext.CancellationToken);
        auth.SignOut();
        completion.SetResult(new([NotificationParsingTests.Notification("old")], null, false, Now, TimeSpan.FromSeconds(60)));
        await load;
        Assert.IsEmpty(page.GetItems());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task BulkUnknownOutcome_ReconcilesRatherThanSendingDuplicate()
    {
        var client = new Mock<INotificationBrowsingClient>();
        client.Setup(c => c.MarkAllReadAsync(Account, null, Now, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Response lost", outcomeUnknown: true));
        client.Setup(c => c.GetNotificationsAsync(Account, new NotificationQuery(UnreadOnly: true),
            null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationPollResult([NotificationParsingTests.Notification("1") with { UpdatedAt = Now }], null, false, null, TimeSpan.FromSeconds(60)));
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = new BulkNotificationReadPage(Account, client.Object, executor, Now, [], () => true, () => Task.CompletedTask);
        await page.SubmitAsync(null, Now);
        await page.SubmitAsync(null, Now);
        client.Verify(c => c.MarkAllReadAsync(Account, null, Now, It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetNotificationsAsync(Account, new NotificationQuery(UnreadOnly: true),
            null, null, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [TestMethod]
    [DataRow("2026-10-03T12:00:00")]
    [DataRow("not a time")]
    public void Timestamp_RequiresExplicitTimezone(string value) =>
        Assert.ThrowsExactly<GitHubApiException>(() => NotificationsPage.Timestamp(value));

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => callback(request);
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed partial class NativePage(Func<bool> current) : ListPage, IDisposable
    {
        internal Func<bool> IsCurrent => current;
        internal bool Disposed { get; private set; }
        public override IListItem[] GetItems() => [];
        public void Dispose() => Disposed = true;
    }
}
