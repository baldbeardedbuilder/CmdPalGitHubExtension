// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public class IssueDetailsPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");

    [TestMethod]
    public async Task Open_LoadsIssueAndOpenInBrowserUsesIssueUrl()
    {
        var issueApiUrl = new Uri("https://api.github.com/repos/octo/tool/issues/42");
        var issue = new GitHubIssue(
            42,
            "Fix keyboard navigation",
            "Description",
            SubjectState.Open,
            new Uri("https://github.com/octo/tool/issues/42"),
            new DateTimeOffset(2025, 5, 31, 18, 30, 0, TimeSpan.Zero),
            "octocat",
            ["mona"],
            ["bug"],
            3);
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        var page = new IssueDetailsPage(auth, client.Object, browser);

        page.LoadIssue(Account, issueApiUrl, "octo/tool");
        await page.CurrentLoad;
        page.HandleSubmit(IssueDetailsActions.OpenInBrowser);

        Assert.AreEqual(issue.WebUrl, browser.LastOpened);
        client.Verify(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Open_ShowsRetryWhenIssueCannotBeLoaded()
    {
        var issueApiUrl = new Uri("https://api.github.com/repos/octo/tool/issues/42");
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("GitHub refused the request."));
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var page = new IssueDetailsPage(auth, client.Object, new FakeBrowser(_ => null));

        page.LoadIssue(Account, issueApiUrl, "octo/tool");
        await page.CurrentLoad;

        var content = (FormContent)page.GetContent().Single();
        Assert.Contains("GitHub refused the request.", content.TemplateJson);
        Assert.AreEqual("Refresh", Assert.ContainsSingle(page.Commands.OfType<CommandContextItem>()).Command!.Name);
    }

    [TestMethod]
    public async Task Dispose_CancelsPendingIssueRequest()
    {
        var issueApiUrl = new Uri("https://api.github.com/repos/octo/tool/issues/42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IIssuesClient>();
        CancellationToken requestToken = default;
        client.Setup(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, Uri _, CancellationToken token) =>
            {
                requestToken = token;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null!;
            });
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var page = new IssueDetailsPage(auth, client.Object, new FakeBrowser(_ => null));

        page.Open(Account, issueApiUrl, "octo/tool");
        var load = page.CurrentLoad;
        await started.Task;
        page.Dispose();
        await load;

        Assert.IsTrue(requestToken.IsCancellationRequested);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task OpeningIssueNotification_NavigatesToDetailsInsteadOfBrowser()
    {
        var issueApiUrl = new Uri("https://api.github.com/repos/octo/tool/issues/42");
        var issue = new GitHubIssue(
            42,
            "Fix keyboard navigation",
            "Description",
            SubjectState.Open,
            new Uri("https://github.com/octo/tool/issues/42"),
            new DateTimeOffset(2025, 5, 31, 18, 30, 0, TimeSpan.Zero),
            "octocat",
            [],
            [],
            0);
        var issueClient = new Mock<IIssuesClient>();
        issueClient.Setup(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        var notificationClient = new Mock<INotificationsClient>();
        var notification = new GitHubNotification("thread", issue.Title, "Issue", issueApiUrl, "octo/tool", null, "subscribed", true, issue.CreatedAt);
        notificationClient.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new NotificationsPageResult([notification], null));
        notificationClient.Setup(c => c.MarkAsReadAsync(Account, "thread", It.IsAny<CancellationToken>()))
            .Callback(() => notification = notification with { Unread = false })
            .Returns(Task.CompletedTask);
        notificationClient.Setup(c => c.GetSubjectAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubjectDetails?)null);
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        var detailsPage = new IssueDetailsPage(auth, issueClient.Object, browser);
        using var notificationsPage = new NotificationsPage(auth, notificationClient.Object, browser, issueDetails: detailsPage);

        notificationsPage.GetItems();
        await notificationsPage.CurrentLoad;
        var item = (NotificationItem)notificationsPage.GetItems().Single();
        var destinationPage = (IssueDetailsPage)item.Command!;
        destinationPage.GetContent();
        await destinationPage.CurrentLoad;
        await notificationsPage.CurrentMutation;

        Assert.IsFalse(((NotificationItem)notificationsPage.GetItems().Single()).Unread);
        Assert.IsNull(browser.LastOpened);
        notificationClient.Verify(c => c.MarkAsReadAsync(Account, "thread", It.IsAny<CancellationToken>()), Times.Once);
        issueClient.Verify(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task DisposingNotificationsPage_CancelsItsIssueDetailsPage()
    {
        var issueApiUrl = new Uri("https://api.github.com/repos/octo/tool/issues/42");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issueClient = new Mock<IIssuesClient>();
        CancellationToken requestToken = default;
        issueClient.Setup(c => c.GetIssueAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, Uri _, CancellationToken token) =>
            {
                requestToken = token;
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null!;
            });
        var notificationClient = new Mock<INotificationsClient>();
        notificationClient.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [new GitHubNotification("thread", "Issue", "Issue", issueApiUrl, "octo/tool", null, "subscribed", true, DateTimeOffset.UtcNow)],
                null));
        notificationClient.Setup(c => c.GetSubjectAsync(Account, issueApiUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubjectDetails?)null);
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        var detailsPage = new IssueDetailsPage(auth, issueClient.Object, browser);
        var notificationsPage = new NotificationsPage(auth, notificationClient.Object, browser, issueDetails: detailsPage);

        notificationsPage.GetItems();
        await notificationsPage.CurrentLoad;
        var destinationPage = (IssueDetailsPage)((NotificationItem)notificationsPage.GetItems().Single()).Command!;
        destinationPage.GetContent();
        var load = destinationPage.CurrentLoad;
        await started.Task;
        notificationsPage.Dispose();
        await load;

        Assert.IsTrue(requestToken.IsCancellationRequested);
        Assert.IsFalse(destinationPage.IsLoading);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(condition());
    }
}
