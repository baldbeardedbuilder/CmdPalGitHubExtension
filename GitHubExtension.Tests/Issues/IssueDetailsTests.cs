// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueDetailsTests
{
    private static readonly string[] ExpectedAssignees = ["@mona", "@hubot"];
    private static readonly string[] ExpectedLabels = ["bug", "help wanted"];
    private static readonly string[] MinimalMetadata = ["Repository", "Comments", "Issue"];

    [TestMethod]
    public void Details_ContainsIssueDescriptionAndMetadata()
    {
        var created = new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var issue = new GitHubIssue(
            42,
            "Keyboard navigation",
            "## Description\n\nKeep **markdown** and [links](https://github.com/o/r).",
            SubjectState.Open,
            new Uri("https://github.com/o/r/issues/42"),
            created,
            "octocat",
            ["mona", "hubot"],
            ["bug", "help wanted"],
            3);
        var details = new IssueDetails(issue, "o/r");
        var item = new ListItem(new NoOpCommand()) { Details = details };

        Assert.AreSame(details, item.Details);
        Assert.AreEqual("#42 Keyboard navigation", details.Title);
        Assert.AreEqual(issue.Body, details.Body);
        Assert.AreEqual("o/r", Text(details, "Repository"));
        Assert.AreEqual("@octocat", Text(details, "Author"));
        Assert.AreEqual(created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Text(details, "Created"));
        Assert.AreEqual("3", Text(details, "Comments"));

        var assignees = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == "Assignees").Data);
        CollectionAssert.AreEqual(ExpectedAssignees, assignees.Tags.Select(tag => tag.Text).ToArray());
        var labels = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == "Labels").Data);
        CollectionAssert.AreEqual(ExpectedLabels, labels.Tags.Select(tag => tag.Text).ToArray());
        var state = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == "State").Data);
        Assert.AreEqual("Open", state.Tags.Single().Text);
        var link = Assert.IsInstanceOfType<IDetailsLink>(details.Metadata.Single(m => m.Key == "Issue").Data);
        Assert.AreEqual("#42", link.Text);
        Assert.AreEqual(issue.WebUrl, link.Link);
        Assert.IsFalse(details.Metadata.Any(m => m.Data is IDetailsCommands));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Details_EmptyDescriptionAndMissingMetadata_AreExplicit(string? body)
    {
        var issue = new GitHubIssue(
            7,
            "Issue",
            body,
            SubjectState.Unknown,
            new Uri("https://github.com/o/r/issues/7"),
            DateTimeOffset.MinValue,
            null,
            [],
            [],
            0);

        var details = new IssueDetails(issue, "o/r");

        Assert.AreEqual("No description provided.", details.Body);
        CollectionAssert.AreEqual(MinimalMetadata, details.Metadata.Select(m => m.Key).ToArray());
    }

    private static string Text(IssueDetails details, string key)
    {
        var data = Assert.IsInstanceOfType<IDetailsLink>(details.Metadata.Single(m => m.Key == key).Data);
        return data.Text;
    }
}
