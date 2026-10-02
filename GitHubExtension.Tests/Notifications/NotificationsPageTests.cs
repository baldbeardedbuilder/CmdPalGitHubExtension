// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
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
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1", "Issue", new Uri("https://api.github.com/repos/o/r/issues/3"))], null));
        var page = CreatePage(client.Object, out var browser);
        page.GetItems();
        await page.CurrentLoad;
        var item = (NotificationItem)page.GetItems().Single();

        ((InvokableCommand)item.Command!).Invoke();

        Assert.AreEqual(new Uri("https://github.com/o/r/issues/3"), browser.LastOpened);
        Assert.IsFalse(item.Unread);
        await WaitFor(() => client.Invocations.Any(i => i.Method.Name == nameof(INotificationsClient.MarkAsReadAsync)));
    }

    [TestMethod]
    public async Task MarkAsDone_RemovesTheItem()
    {
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([NotificationParsingTests.Notification("1"), NotificationParsingTests.Notification("2")], null));
        var page = CreatePage(client.Object, out _);
        page.GetItems();
        await page.CurrentLoad;

        page.MarkAsDone((NotificationItem)page.GetItems()[0]);

        Assert.HasCount(1, page.GetItems());
        await WaitFor(() => client.Invocations.Any(i => i.Method.Name == nameof(INotificationsClient.MarkAsDoneAsync)));
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

    private static NotificationsPage CreatePage(INotificationsClient client, out FakeBrowser browser)
    {
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
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
