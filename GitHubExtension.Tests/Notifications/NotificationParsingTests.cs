// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Notifications;

[TestClass]
public class NotificationParsingTests
{
    [TestMethod]
    public void ParseNotification_ReadsTheFieldsWeShow()
    {
        using var json = JsonDocument.Parse("""
            {
              "id": "42",
              "unread": true,
              "reason": "review_requested",
              "updated_at": "2025-01-02T03:04:05Z",
              "subject": { "title": "Fix the thing", "type": "PullRequest", "url": "https://api.github.com/repos/o/r/pulls/7" },
              "repository": { "full_name": "o/r", "html_url": "https://github.com/o/r" }
            }
            """);

        var n = NotificationsClient.ParseNotification(json.RootElement)!;

        Assert.AreEqual("42", n.Id);
        Assert.AreEqual("Fix the thing", n.Title);
        Assert.AreEqual("PullRequest", n.SubjectType);
        Assert.AreEqual("o/r", n.RepositoryFullName);
        Assert.IsTrue(n.Unread);
        Assert.AreEqual(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), n.UpdatedAt);
    }

    [TestMethod]
    [DataRow("""{ "id": "1" }""")]
    [DataRow("null")]
    [DataRow("42")]
    public void ParseNotification_RejectsJunk(string body)
    {
        using var json = JsonDocument.Parse(body);
        Assert.ThrowsExactly<GitHubApiException>(() => NotificationsClient.ParseNotification(json.RootElement));
    }

    [TestMethod]
    [DataRow("""{ "state": "open" }""", "Open")]
    [DataRow("""{ "state": "open", "draft": true }""", "Draft")]
    [DataRow("""{ "state": "closed", "merged": true }""", "Merged")]
    [DataRow("""{ "state": "closed", "merged_at": "2025-01-01T00:00:00Z" }""", "Merged")]
    [DataRow("""{ "state": "closed" }""", "Closed")]
    [DataRow("""{ "state": "closed", "state_reason": "not_planned" }""", "NotPlanned")]
    [DataRow("""{ }""", "Unknown")]
    public void ParseSubject_MapsState(string body, string expected)
    {
        using var json = JsonDocument.Parse(body);
        Assert.AreEqual(Enum.Parse<SubjectState>(expected), NotificationsClient.ParseSubject(json.RootElement).State);
    }

    [TestMethod]
    public void ParseSubject_IssueIncludesDetails()
    {
        using var json = JsonDocument.Parse("""
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

        Assert.IsNull(subject.PullRequest);
        Assert.AreEqual(9, subject.Issue!.Number);
        Assert.AreEqual("Issue description", subject.Issue.Body);
        Assert.AreEqual("octocat", subject.Issue.Author);
        Assert.AreEqual("mona", subject.Issue.Assignees.Single());
    }

    [TestMethod]
    public void ParseNextLink_FindsNext()
    {
        var header = "<https://api.github.com/notifications?page=3>; rel=\"last\", <https://api.github.com/notifications?page=2>; rel=\"next\"";
        Assert.AreEqual(new Uri("https://api.github.com/notifications?page=2"), GitHubRest.ParseNextLink(header));
        Assert.IsNull(GitHubRest.ParseNextLink("<https://api.github.com/notifications?page=1>; rel=\"prev\""));
        Assert.IsNull(GitHubRest.ParseNextLink(null));
    }

    [TestMethod]
    [DataRow(30, "just now")]
    [DataRow(45 * 60, "45m ago")]
    [DataRow(3 * 3600, "3h ago")]
    [DataRow(2 * 86400, "2d ago")]
    public void RelativeTime_FormatsLikeGitHub(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(expected, NotificationFormatting.RelativeTime(now.AddSeconds(-secondsAgo), now));
    }

    [TestMethod]
    [DataRow("PullRequest", "https://api.github.com/repos/o/r/pulls/7", "https://github.com/o/r/pull/7")]
    [DataRow("Issue", "https://api.github.com/repos/o/r/issues/9", "https://github.com/o/r/issues/9")]
    [DataRow("Commit", "https://api.github.com/repos/o/r/commits/abc", "https://github.com/o/r/commit/abc")]
    [DataRow("Discussion", null, "https://github.com/o/r/discussions")]
    [DataRow("CheckSuite", null, "https://github.com/o/r/actions")]
    [DataRow("Release", "https://api.github.com/repos/o/r/releases/1", "https://github.com/o/r/releases")]
    public void WebUrl_MapsApiUrlsToTheBrowser(string type, string? api, string expected)
    {
        var n = Notification("1", type, api is null ? null : new Uri(api));
        Assert.AreEqual(new Uri(expected), NotificationFormatting.WebUrl(GitHubHost.GitHubDotCom, n));
    }

    [TestMethod]
    public void StateTag_UsesGitHubStates()
    {
        Assert.AreEqual("Open", NotificationFormatting.StateTag("PullRequest", SubjectState.Open)!.Text);
        Assert.AreEqual("Merged", NotificationFormatting.StateTag("PullRequest", SubjectState.Merged)!.Text);
        Assert.AreEqual("Closed", NotificationFormatting.StateTag("Issue", SubjectState.Closed)!.Text);
        Assert.IsNull(NotificationFormatting.StateTag("Issue", SubjectState.Unknown));
    }

    internal static GitHubNotification Notification(string id, string type = "Issue", Uri? api = null, string title = "Title", string repo = "o/r", bool unread = true) => new(
        id,
        title,
        type,
        api,
        repo,
        new Uri($"https://github.com/{repo}"),
        "subscribed",
        unread,
        new DateTimeOffset(2025, 6, 1, 11, 15, 0, TimeSpan.Zero));
}
