// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Notifications;

[TestClass]
public sealed class NotificationIssueMutationTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly Uri ApiUrl = new("https://api.github.com/repos/o/r/issues/3");

    [TestMethod]
    public async Task ConfirmedIssueChange_RefreshesCachedPreviewWithoutMarkingReadOrDone()
    {
        var before = new GitHubIssue(3, "Title", "Body", SubjectState.Open, new Uri("https://github.com/o/r/issues/3"),
            DateTimeOffset.UtcNow, "octocat", [], [], 0);
        var after = before with { State = SubjectState.Closed, Labels = ["fixed"] };
        var authoritative = before;
        var notifications = new Mock<INotificationsClient>();
        notifications.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [NotificationParsingTests.Notification("thread", "Issue", ApiUrl) with { Unread = false }], null));
        notifications.Setup(c => c.GetSubjectAsync(Account, ApiUrl, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SubjectDetails(authoritative.State, authoritative.WebUrl, Issue: authoritative));
        var issues = new Mock<IIssuesClient>();
        issues.Setup(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>())).ReturnsAsync(before);
        var mutations = new Mock<IIssueMutationsClient>();
        mutations.SetupSequence(c => c.GetMutationIssueAsync(Account, "o/r", 3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(before).ReturnsAsync(after);
        mutations.Setup(c => c.ChangeStateAsync(Account, "o/r", 3, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .Callback(() => authoritative = after).ReturnsAsync(after);
        using var auth = Auth();
        var browser = new FakeBrowser(_ => null);
        using var template = new IssueDetailsPage(auth, issues.Object, browser, mutations.Object);
        using var page = new NotificationsPage(auth, notifications.Object, browser, issueDetails: template);
        page.GetItems();
        await page.CurrentLoad;
        var oldItem = Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single());
        Assert.AreEqual(SubjectState.Open, oldItem.Subject!.State);
        var detail = Assert.IsInstanceOfType<IssueDetailsPage>(oldItem.Command);
        detail.GetContent();
        await detail.CurrentLoad;
        Assert.ContainsSingle(detail.GetContent().OfType<FormContent>())
            .SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.CloseCompleted}}"}""");
        Assert.ContainsSingle(detail.GetContent().OfType<FormContent>())
            .SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.Confirm}}"}""");
        await detail.CurrentMutation;
        await page.CurrentLoad;
        var refreshed = Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single());
        Assert.AreEqual(after, refreshed.Subject!.Issue);
        Assert.AreEqual(SubjectState.Closed, refreshed.Subject.State);
        Assert.IsFalse(refreshed.Unread);
        notifications.Verify(c => c.GetSubjectAsync(Account, ApiUrl, It.IsAny<CancellationToken>()), Times.Exactly(2));
        notifications.Verify(c => c.MarkAsReadAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        notifications.Verify(c => c.MarkAsDoneAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        auth.SignOut();
        await page.RefreshAfterIssueMutationAsync(oldItem);
        notifications.Verify(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
}
