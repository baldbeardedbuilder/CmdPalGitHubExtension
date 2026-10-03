// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Notifications;

[TestClass]
public sealed class ThreadSubscriptionTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow((int)ThreadSubscriptionAction.Subscribe, "PUT", """{"ignored":false}""")]
    [DataRow((int)ThreadSubscriptionAction.Ignore, "PUT", """{"ignored":true}""")]
    [DataRow((int)ThreadSubscriptionAction.Unsubscribe, "DELETE", null)]
    public async Task Client_UsesSubscriptionEndpointNotDoneEndpoint(int actionValue, string method, string? body)
    {
        Assert.IsTrue(GitHubHost.TryParse("git.example.com", out var host));
        var account = Account with { Host = host! };
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.AreEqual(method, request.Method.Method);
            Assert.AreEqual("https://git.example.com/api/v3/notifications/threads/123/subscription", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(Account.Token, request.Headers.Authorization!.Parameter);
            Assert.AreEqual(body, request.Content is null ? null : await request.Content.ReadAsStringAsync(TestContext.CancellationToken));
            return new HttpResponseMessage(method == "PUT" ? HttpStatusCode.OK : HttpStatusCode.NoContent)
            {
                Content = method == "PUT" ? new StringContent("""{"subscribed":true,"ignored":false}""") : null,
            };
        }));
        await new NotificationsClient(http).SetSubscriptionAsync(account, "123", (ThreadSubscriptionAction)actionValue, TestContext.CancellationToken);
    }

    [TestMethod]
    public async Task MissingSubscription_RestoresRepositoryRulesNotIgnoredState()
    {
        using var http = new HttpClient(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/subscription", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"123"}""") })));
        var state = await new NotificationsClient(http).GetSubscriptionAsync(Account, "123", TestContext.CancellationToken);
        Assert.AreEqual(ThreadSubscription.Default, state);
        Assert.IsFalse(state.Ignored);
        Assert.IsFalse(state.HasSubscription);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task Client_FailureIsNotDefaultSubscription(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(status))));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetSubscriptionAsync(Account, "123", TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Ignore, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task InaccessibleThread_IsNotReportedAsDefaultSubscription()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetSubscriptionAsync(Account, "123", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Unsubscribe_AcceptsAuthoritativeFalseFlagsInSuccessfulResponse()
    {
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        subscriptions.SetupSequence(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ThreadSubscription(true, false))
            .ReturnsAsync(new ThreadSubscription(false, false));
        subscriptions.Setup(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Unsubscribe, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        await page.SetAsync(new ThreadSubscription(true, false), ThreadSubscriptionAction.Unsubscribe, TestContext.CancellationToken);
        Assert.AreEqual(new ThreadSubscription(false, false), page.Subscription);
        Assert.Contains("confirmed", page.GetItems()[0].Subtitle);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("""{"subscribed":true,"ignored":"false"}""")]
    [DataRow("""{"subscribed":null,"ignored":false}""")]
    public void Parse_RejectsUnknownOrMalformedState(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.ThrowsExactly<GitHubApiException>(() => NotificationsClient.ParseSubscription(json.RootElement));
    }

    [TestMethod]
    public async Task Client_PreservesSsoRecovery()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-GitHub-SSO", "required; url=https://github.com/orgs/o/sso");
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetSubscriptionAsync(Account, "123", TestContext.CancellationToken));
        Assert.AreEqual(new Uri("https://github.com/orgs/o/sso"), error.AuthorizeUrl);
    }

    [TestMethod]
    public async Task UnsubscribeAndIgnore_UseDifferentAuthoritativeStatesAndKeepReadDoneSeparate()
    {
        var notifications = new Mock<INotificationsClient>();
        notifications.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("123")], null));
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        var state = new ThreadSubscription(true, false);
        subscriptions.Setup(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
        subscriptions.Setup(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Unsubscribe, It.IsAny<CancellationToken>()))
            .Callback(() => state = ThreadSubscription.Default).Returns(Task.CompletedTask);
        subscriptions.Setup(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Ignore, It.IsAny<CancellationToken>()))
            .Callback(() => state = new ThreadSubscription(false, true)).Returns(Task.CompletedTask);
        using var auth = Auth();
        using var page = new NotificationsPage(auth, notifications.Object, new FakeBrowser(_ => null), subscriptionsClient: subscriptions.Object);
        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single());
        var subscriptionPage = Assert.IsInstanceOfType<ThreadSubscriptionPage>(
            item.MoreCommands.OfType<CommandContextItem>().Single(command => command.Command is ThreadSubscriptionPage).Command);
        subscriptionPage.GetItems();
        await subscriptionPage.CurrentLoad;
        await subscriptionPage.SetAsync(state, ThreadSubscriptionAction.Unsubscribe, TestContext.CancellationToken);
        Assert.AreEqual(ThreadSubscription.Default, subscriptionPage.Subscription);
        Assert.AreEqual("Repository notification rules apply", subscriptionPage.GetItems()[0].Title);
        var unsubscribe = subscriptionPage.Confirmation(ThreadSubscription.Default, ThreadSubscriptionAction.Unsubscribe);
        Assert.Contains("watched repository", Assert.IsInstanceOfType<FormContent>(unsubscribe.GetContent().Single()).TemplateJson);
        await subscriptionPage.SetAsync(state, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        Assert.IsTrue(subscriptionPage.Subscription!.Ignored);
        Assert.AreEqual("Ignored", subscriptionPage.GetItems()[0].Title);
        Assert.AreSame(item, page.GetItems().Single());
        Assert.IsTrue(item.Unread);
        notifications.Verify(c => c.MarkAsReadAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        notifications.Verify(c => c.MarkAsDoneAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        auth.SignOut();
        Assert.IsEmpty(subscriptionPage.GetItems());
    }

    [TestMethod]
    public async Task Subscribe_ClearsIgnoredStateAfterAuthoritativeRefresh()
    {
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        var state = new ThreadSubscription(false, true);
        subscriptions.Setup(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
        subscriptions.Setup(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Subscribe, It.IsAny<CancellationToken>()))
            .Callback(() => state = new ThreadSubscription(true, false)).Returns(Task.CompletedTask);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        await page.SetAsync(state, ThreadSubscriptionAction.Subscribe, TestContext.CancellationToken);
        Assert.AreEqual(new ThreadSubscription(true, false), page.Subscription);
        subscriptions.Verify(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task SubscriptionRace_DoesNotOverwriteChangedState()
    {
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        var fresh = new ThreadSubscription(false, true);
        subscriptions.Setup(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>())).ReturnsAsync(fresh);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        await page.SetAsync(new ThreadSubscription(true, false), ThreadSubscriptionAction.Unsubscribe, TestContext.CancellationToken);
        subscriptions.Verify(c => c.SetSubscriptionAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<ThreadSubscriptionAction>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.AreEqual(fresh, page.Subscription);
        Assert.Contains("changed", page.GetItems()[0].Subtitle);
    }

    [TestMethod]
    public async Task UnknownWrite_ReconcilesWithoutAutomaticRetry()
    {
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        var state = ThreadSubscription.Default;
        subscriptions.Setup(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
        subscriptions.Setup(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Ignore, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Unknown outcome", outcomeUnknown: true));
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        await page.SetAsync(state, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        state = new ThreadSubscription(false, true);
        await page.SetAsync(ThreadSubscription.Default, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        subscriptions.Verify(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Ignore, It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(state, page.Subscription);
    }

    [TestMethod]
    public async Task AccountChangeDuringValidation_PreventsSubscriptionWrite()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        subscriptions.Setup(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ThreadSubscription.Default;
            });
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        var mutation = page.SetAsync(ThreadSubscription.Default, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        await started.Task.WaitAsync(TestContext.CancellationToken);
        auth.SignOut();
        await mutation;
        subscriptions.Verify(c => c.SetSubscriptionAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<ThreadSubscriptionAction>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsEmpty(page.GetItems());
    }

    [TestMethod]
    public async Task ClosingOriginatingPageDuringValidation_PreventsWrite()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        subscriptions.Setup(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ThreadSubscription.Default;
            });
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        var mutation = page.SetAsync(ThreadSubscription.Default, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        await started.Task.WaitAsync(TestContext.CancellationToken);
        page.Dispose();
        await mutation;
        subscriptions.Verify(c => c.SetSubscriptionAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<ThreadSubscriptionAction>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task VerificationFailureAfterWrite_DoesNotAllowBlindRetry()
    {
        var subscriptions = new Mock<IThreadSubscriptionsClient>();
        subscriptions.SetupSequence(c => c.GetSubscriptionAsync(Account, "123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(ThreadSubscription.Default)
            .ThrowsAsync(new GitHubApiException("Forbidden during verification"))
            .ReturnsAsync(new ThreadSubscription(false, true));
        subscriptions.Setup(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Ignore, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = SubscriptionPage(subscriptions.Object, executor);
        await page.SetAsync(ThreadSubscription.Default, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        Assert.IsNull(page.Subscription);
        await page.SetAsync(ThreadSubscription.Default, ThreadSubscriptionAction.Ignore, TestContext.CancellationToken);
        Assert.IsTrue(page.Subscription!.Ignored);
        subscriptions.Verify(c => c.SetSubscriptionAsync(Account, "123", ThreadSubscriptionAction.Ignore, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static ThreadSubscriptionPage SubscriptionPage(IThreadSubscriptionsClient client, MutationExecutor executor) =>
        new(client, executor, Account, "123", () => true, new FakeBrowser(_ => null));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
