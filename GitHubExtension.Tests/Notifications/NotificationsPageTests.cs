// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Notifications;

[TestClass]
public class NotificationsPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");

    [TestMethod]
    public async Task GetItems_LoadsAndFillsInState()
    {
        var api = new Uri("https://api.github.com/repos/o/r/pulls/7");
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest", api)], null));
        client.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectDetails(SubjectState.Merged, new Uri("https://github.com/o/r/pull/7")));
        var page = CreatePage(client.Object, out _);

        page.GetItems();
        await page.CurrentLoad;

        var item = (NotificationItem)page.GetItems().Single();
        Assert.AreEqual("Title", item.Title);
        Assert.AreEqual("o/r \u00B7 45m ago", item.Subtitle);
        Assert.AreEqual("Merged", item.Tags.Single().Text);
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task Filter_MatchesEveryTerm()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [
                    NotificationParsingTests.Notification("1", title: "Fix login", repo: "o/web"),
                    NotificationParsingTests.Notification("2", title: "Fix build", repo: "o/api"),
                ],
                null));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        page.SearchText = "fix api";

        Assert.AreEqual("Fix build", page.GetItems().Single().Title);
    }

    [TestMethod]
    public async Task LoadMore_AppendsTheNextPage()
    {
        var next = new Uri("https://api.github.com/notifications?page=2");
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1")], next));
        client.Setup(c => c.GetNotificationsAsync(Account, next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("2")], null));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;
        Assert.IsTrue(page.HasMoreItems);

        page.LoadMore();
        await page.CurrentLoad;

        Assert.HasCount(2, page.GetItems());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    public async Task Open_LaunchesBrowserAndMarksRead()
    {
        var read = false;
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NotificationsPageResult([NotificationParsingTests.Notification("1", "Issue", new Uri("https://api.github.com/repos/o/r/issues/3")) with { Unread = !read }], null));
        client.Setup(c => c.MarkAsReadAsync(Account, "1", It.IsAny<CancellationToken>()))
            .Callback(() => read = true).Returns(Task.CompletedTask);
        var page = CreatePage(client.Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();

        var result = ((InvokableCommand)item.Command!).Invoke();

        Assert.AreEqual(new Uri("https://github.com/o/r/issues/3"), browser.LastOpened);
        Assert.AreEqual(CommandResultKind.Dismiss, result.Kind);
        await page.CurrentMutation;
        Assert.IsFalse(((NotificationItem)page.GetItems().Single()).Unread);
        client.Verify(c => c.MarkAsReadAsync(Account, "1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task OpenRepositoryContextAction_LaunchesBrowserAndDismissesPalette()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1")], null));
        var page = CreatePage(client.Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();
        var command = item.MoreCommands
            .OfType<CommandContextItem>()
            .Single(context => context.Command?.Name == "Open repository")
            .Command;

        var result = ((InvokableCommand)command!).Invoke();

        Assert.AreEqual(new Uri("https://github.com/o/r"), browser.LastOpened);
        Assert.AreEqual(CommandResultKind.Dismiss, result.Kind);
    }

    [TestMethod]
    public async Task MarkAsDone_RemovesTheItem()
    {
        var done = false;
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NotificationsPageResult(done
                ? [NotificationParsingTests.Notification("2")]
                : [NotificationParsingTests.Notification("1"), NotificationParsingTests.Notification("2")], null));
        client.Setup(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>()))
            .Callback(() => done = true).Returns(Task.CompletedTask);
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        page.MarkAsDone((NotificationItem)page.GetItems()[0]);

        await page.CurrentMutation;
        Assert.HasCount(1, page.GetItems());
        client.Verify(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task LoadFailure_ShowsTheError()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("nope"));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("nope", page.EmptyContent!.Subtitle);
    }

    [TestMethod]
    public async Task PullRequest_PreviewLoadsWithoutChangingOpenBehavior()
    {
        var api = new Uri("https://api.github.com/repos/o/r/pulls/7");
        using var json = System.Text.Json.JsonDocument.Parse(PullRequestDetailsTests.Payload);
        var subject = NotificationsClient.ParseSubject(json.RootElement);
        var pending = new TaskCompletionSource<SubjectDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest", api)], null));
        client.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>())).Returns(pending.Task);
        var page = CreatePage(client.Object, out var browser);
        page.GetItems();
        var load = page.CurrentLoad;
        await WaitFor(() => client.Invocations.Any(i => i.Method.Name == nameof(INotificationsClient.GetSubjectAsync)));
        var item = (NotificationItem)page.GetItems().Single();
        Assert.IsTrue(page.ShowDetails);
        Assert.AreEqual("Loading pull request details...", item.Details!.Body);
        Assert.IsTrue(item.Unread);
        Assert.IsNull(browser.LastOpened);

        pending.SetResult(subject);
        await load;

        Assert.AreEqual("#7 Fix login", item.Details.Title);
        Assert.AreEqual(subject.PullRequest!.Body, item.Details.Body);
        ((InvokableCommand)item.Command!).Invoke();
        Assert.AreEqual(subject.WebUrl, browser.LastOpened);
        Assert.IsTrue(item.Unread);
        await page.CurrentMutation;
        await page.RefreshAsync();
        Assert.AreEqual("#7 Fix login", page.GetItems().Single().Details!.Title);
        client.Verify(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PullRequest_SubjectFailureShowsDetailsError(bool throws)
    {
        var api = new Uri("https://api.github.com/repos/o/r/pulls/7");
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest", api)], null));
        var lookup = client.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()));
        if (throws)
        {
            lookup.ThrowsAsync(new GitHubApiException("Couldn't reach GitHub."));
        }
        else
        {
            lookup.ReturnsAsync((SubjectDetails?)null);
        }

        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        var item = page.GetItems().Single();
        Assert.AreEqual("Title", item.Details!.Title);
        Assert.AreEqual(throws ? "Couldn't reach GitHub." : "Couldn't load pull request details. Try refreshing notifications or open it on GitHub.", item.Details.Body);
    }

    [TestMethod]
    public async Task PullRequest_SsoFailureOffersAuthorizeLink()
    {
        var api = new Uri("https://api.github.com/repos/o/r/pulls/7");
        var authorize = new Uri("https://github.com/orgs/o/sso?authorization_request=x");
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest", api)], null));
        client.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("SSO needed.", authorizeUrl: authorize));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        var item = (NotificationItem)page.GetItems().Single();
        Assert.AreEqual("SSO needed.", item.Details!.Body);
        var link = (DetailsLink)item.Details.Metadata.Single().Data;
        Assert.AreEqual(authorize, link.Link);
        Assert.AreEqual(authorize, item.AuthorizeUrl);
        Assert.AreEqual("Authorize single sign-on", ((CommandContextItem)item.MoreCommands[0]).Command!.Name);
    }

    [TestMethod]
    public async Task Issue_WithoutSubjectUrlShowsUnavailableDetails()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1")], null));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.AreEqual("No issue details are available. Open it on GitHub to learn more.", page.GetItems().Single().Details!.Body);
    }

    [TestMethod]
    public async Task Issue_PreviewLoadsDetailsFromSubject()
    {
        var api = new Uri("https://api.github.com/repos/o/r/issues/9");
        using var json = System.Text.Json.JsonDocument.Parse("""
            {
              "number": 9,
              "title": "Fix the thing",
              "body": "Issue description",
              "state": "open",
              "html_url": "https://github.com/o/r/issues/9",
              "created_at": "2025-01-02T03:04:05Z",
              "user": { "login": "octocat" },
              "assignees": [{ "login": "mona" }],
              "labels": [{ "name": "bug" }],
              "comments": 2
            }
            """);
        var subject = NotificationsClient.ParseSubject(json.RootElement);
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "Issue", api)], null));
        client.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subject);
        var page = CreatePage(client.Object, out _);

        page.GetItems();
        await page.CurrentLoad;

        var details = page.GetItems().Single().Details!;
        Assert.AreEqual("#9 Fix the thing", details.Title);
        Assert.AreEqual("Issue description", details.Body);
        Assert.AreEqual("o/r", ((DetailsLink)details.Metadata.Single(m => m.Key == "Repository").Data).Text);
        Assert.AreEqual("@octocat", ((DetailsLink)details.Metadata.Single(m => m.Key == "Author").Data).Text);
    }

    [TestMethod]
    public async Task PullRequest_WithoutSubjectUrlShowsUnavailableDetails()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest")], null));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.AreEqual("No pull request details are available. Open it on GitHub to learn more.", page.GetItems().Single().Details!.Body);
        client.Verify(c => c.GetSubjectAsync(It.IsAny<GitHubAccount>(), It.IsAny<Uri>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task PullRequest_RefreshIgnoresStaleSubjectDetails()
    {
        var api = new Uri("https://api.github.com/repos/o/r/pulls/7");
        using var json = System.Text.Json.JsonDocument.Parse(PullRequestDetailsTests.Payload);
        var original = NotificationsClient.ParseSubject(json.RootElement);
        var updated = original with { PullRequest = original.PullRequest! with { Title = "Updated title" } };
        var pending = new TaskCompletionSource<SubjectDetails?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest", api)], null));
        client.SetupSequence(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .Returns(pending.Task)
            .ReturnsAsync(updated);
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        var oldLoad = page.CurrentLoad;
        await WaitFor(() => client.Invocations.Any(i => i.Method.Name == nameof(INotificationsClient.GetSubjectAsync)));

        await page.RefreshAsync();
        pending.SetResult(original);
        await oldLoad;
        await page.RefreshAsync();

        Assert.AreEqual("#7 Updated title", page.GetItems().Single().Details!.Title);
        client.Verify(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task PullRequest_RefreshRetriesMissingDetails()
    {
        var api = new Uri("https://api.github.com/repos/o/r/pulls/7");
        using var json = System.Text.Json.JsonDocument.Parse(PullRequestDetailsTests.Payload);
        var subject = NotificationsClient.ParseSubject(json.RootElement);
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "PullRequest", api)], null));
        client.SetupSequence(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .ReturnsAsync(subject with { PullRequest = null })
            .ReturnsAsync(subject);
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;
        Assert.AreEqual("Couldn't load pull request details. Try refreshing notifications or open it on GitHub.", page.GetItems().Single().Details!.Body);

        await page.RefreshAsync();

        Assert.AreEqual("#7 Fix login", page.GetItems().Single().Details!.Title);
        client.Verify(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static NotificationsPage CreatePage(INotificationsClient client, out FakeBrowser browser)
        => CreatePage(client, out browser, out _);

    [TestMethod]
    public async Task MarkRead_FailureKeepsUnreadAndDisplaysSsoError()
    {
        var client = new Mock<INotificationsClient>();
        var authorize = new Uri("https://github.com/orgs/example/sso");
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1")], null));
        client.Setup(c => c.MarkAsReadAsync(Account, "1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Authorize access.", authorizeUrl: authorize));
        using var page = CreatePage(client.Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();
        page.MarkAsRead(item);
        await page.CurrentMutation;
        Assert.IsTrue(item.Unread);
        var error = page.GetItems().Last();
        Assert.AreEqual("Authorize access.", error.Subtitle);
        ((InvokableCommand)error.Command!).Invoke();
        Assert.AreEqual(authorize, browser.LastOpened);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MarkDone_SessionChangeCancelsAndSuppressesLateFailure(bool dispose)
    {
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1")], null));
        client.Setup(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();
        page.MarkAsDone(item);
        var task = page.CurrentMutation;
        var token = await started.Task;
        page.MarkAsDone(item);
        if (dispose)
        {
            page.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        Assert.IsTrue(token.IsCancellationRequested);
        response.SetException(new GitHubApiException("Late failure."));
        await task;
        await page.CurrentMutation;
        Assert.IsFalse(page.GetItems().Any(i => i.Subtitle == "Late failure."));
        client.Verify(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task MarkDone_PendingWriteReconcilesWithoutResubmission()
    {
        var completed = false;
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NotificationsPageResult(completed ? [] : [NotificationParsingTests.Notification("1")], null));
        client.Setup(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();
        page.MarkAsDone(item);
        await page.CurrentMutation;
        Assert.Contains("still be updating", page.GetItems().Last().Subtitle);
        completed = true;
        page.MarkAsDone(item);
        await page.CurrentMutation;
        Assert.IsEmpty(page.GetItems());
        client.Verify(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static NotificationsPage CreatePage(INotificationsClient client, out FakeBrowser browser, out AuthService auth)
    {
        auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.GetUtcNow()).Returns(new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero));
        return new NotificationsPage(auth, client, browser, time.Object);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(condition());
    }
}
