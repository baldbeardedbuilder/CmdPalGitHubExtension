// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public class IssueParsingTests
{
    [TestMethod]
    public void ParseIssue_ReadsIssueDetails()
    {
        using var json = JsonDocument.Parse(
            """
            {
              "number": 42,
              "title": "Fix keyboard navigation",
              "body": "Focus gets lost after closing the menu.",
              "state": "open",
              "html_url": "https://github.com/octo/tool/issues/42",
              "created_at": "2025-05-31T18:30:00Z",
              "user": { "login": "octocat" },
              "assignees": [{ "login": "mona" }],
              "labels": [{ "name": "bug" }, { "name": "good first issue" }],
              "comments": 3
            }
            """);

        var issue = IssuesClient.ParseIssue(json.RootElement);

        Assert.AreEqual(42, issue.Number);
        Assert.AreEqual("Fix keyboard navigation", issue.Title);
        Assert.AreEqual("Focus gets lost after closing the menu.", issue.Body);
        Assert.AreEqual(SubjectState.Open, issue.State);
        Assert.AreEqual(new Uri("https://github.com/octo/tool/issues/42"), issue.WebUrl);
        Assert.AreEqual("octocat", issue.Author);
        Assert.AreEqual("mona", issue.Assignees.Single());
        Assert.AreEqual(2, issue.Labels.Count);
        Assert.AreEqual("bug", issue.Labels[0]);
        Assert.AreEqual("good first issue", issue.Labels[1]);
        Assert.AreEqual(3, issue.Comments);
    }

    [TestMethod]
    public void ParseIssue_MapsNotPlannedState()
    {
        using var json = JsonDocument.Parse(
            """{ "state": "closed", "state_reason": "not_planned", "html_url": "https://github.com/o/r/issues/1" }""");

        var issue = IssuesClient.ParseIssue(json.RootElement);

        Assert.AreEqual(SubjectState.NotPlanned, issue.State);
    }
}
