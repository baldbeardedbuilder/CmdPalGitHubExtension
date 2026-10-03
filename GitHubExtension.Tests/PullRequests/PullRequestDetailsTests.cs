// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class PullRequestDetailsTests
{
    private static readonly string[] ExpectedLabels = ["bug", "help wanted"];
    private static readonly string[] MinimalMetadata = ["Pull request"];

    internal const string Payload = """
        {
          "number": 7,
          "title": "Fix login",
          "body": "## Why\n\nKeep **markdown** and [links](https://github.com/o/r).",
          "html_url": "https://github.example.com/o/r/pull/7",
          "state": "open",
          "user": { "login": "octocat" },
          "head": { "label": "contributor:fix-login", "ref": "fix-login", "repo": null },
          "base": { "label": "o:main", "ref": "main", "repo": { "full_name": "o/r" } },
          "labels": [{ "name": "bug" }, { "name": "help wanted" }],
          "created_at": "2025-06-01T10:00:00Z",
          "updated_at": "2025-06-01T11:00:00Z",
          "commits": 2,
          "changed_files": 3,
          "additions": 12,
          "deletions": 0
        }
        """;

    [TestMethod]
    public void Details_FromSubjectPayload_IsReusableReadOnlyMetadata()
    {
        using var json = JsonDocument.Parse(Payload);
        var subject = NotificationsClient.ParseSubject(json.RootElement);
        Assert.IsNotNull(subject.PullRequest);

        var details = new PullRequestDetails(subject.PullRequest);
        var item = new ListItem(new NoOpCommand()) { Details = details };

        Assert.AreSame(details, item.Details);
        Assert.AreEqual("#7 Fix login", details.Title);
        Assert.AreEqual("## Why\n\nKeep **markdown** and [links](https://github.com/o/r).", details.Body);
        Assert.AreEqual("o/r", Text(details, "Repository"));
        Assert.AreEqual("@octocat", Text(details, "Author"));
        Assert.AreEqual("contributor:fix-login", Text(details, "From"));
        Assert.AreEqual("o:main", Text(details, "Into"));
        Assert.AreEqual("2", Text(details, "Commits"));
        Assert.AreEqual("3", Text(details, "Changed files"));
        Assert.AreEqual("12", Text(details, "Additions"));
        Assert.AreEqual("0", Text(details, "Deletions"));
        Assert.AreEqual(new DateTimeOffset(2025, 6, 1, 10, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Text(details, "Created"));
        Assert.AreEqual(new DateTimeOffset(2025, 6, 1, 11, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Text(details, "Updated"));
        var labels = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == "Labels").Data);
        CollectionAssert.AreEqual(ExpectedLabels, labels.Tags.Select(t => t.Text).ToArray());
        var link = Assert.IsInstanceOfType<IDetailsLink>(details.Metadata.Single(m => m.Key == "Pull request").Data);
        Assert.AreEqual(new Uri("https://github.example.com/o/r/pull/7"), link.Link);
        Assert.AreEqual("#7", link.Text);
        Assert.IsFalse(details.Metadata.Any(m => m.Data is IDetailsCommands));
    }

    [TestMethod]
    [DataRow("open", false, false, "Open")]
    [DataRow("open", true, false, "Draft")]
    [DataRow("closed", false, false, "Closed")]
    [DataRow("closed", true, false, "Closed")]
    [DataRow("closed", true, true, "Merged")]
    public void Details_StateIncludesTextAndIcon(string state, bool draft, bool merged, string expected)
    {
        using var json = JsonDocument.Parse($$"""
            {
              "number": 7, "title": "PR", "html_url": "https://github.com/o/r/pull/7",
              "head": {}, "base": {}, "state": "{{state}}",
              "draft": {{draft.ToString().ToLowerInvariant()}}, "merged": {{merged.ToString().ToLowerInvariant()}}
            }
            """);
        var details = new PullRequestDetails(NotificationsClient.ParseSubject(json.RootElement).PullRequest!);
        var stateTags = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == "State").Data);
        var tag = stateTags.Tags.Single();
        Assert.AreEqual(expected, tag.Text);
        Assert.IsNotNull(tag.Icon);
    }

    [TestMethod]
    public void Details_StateUsesMergedAtFromPullRequestResponse()
    {
        using var json = JsonDocument.Parse("""
            {
              "number": 7, "title": "PR", "html_url": "https://github.com/o/r/pull/7",
              "head": {}, "base": {}, "state": "closed", "merged_at": "2025-01-01T00:00:00Z"
            }
            """);
        var details = new PullRequestDetails(NotificationsClient.ParseSubject(json.RootElement).PullRequest!);
        var stateTags = Assert.IsInstanceOfType<IDetailsTags>(details.Metadata.Single(m => m.Key == "State").Data);

        Assert.AreEqual("Merged", stateTags.Tags.Single().Text);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Details_EmptyDescriptionAndMissingMetadata_AreExplicit(string? body)
    {
        var details = new PullRequestDetails(new GitHubPullRequest
        {
            Number = 7,
            Title = "PR",
            Body = body,
            WebUrl = new Uri("https://github.com/o/r/pull/7"),
            State = SubjectState.Unknown,
        });

        Assert.AreEqual("No description provided.", details.Body);
        CollectionAssert.AreEqual(MinimalMetadata, details.Metadata.Select(m => m.Key).ToArray());
    }

    [TestMethod]
    public void ParseSubject_IssueDoesNotCreatePullRequestDetails()
    {
        using var json = JsonDocument.Parse("""
            {"number":7,"title":"Issue","state":"open","pull_request":{"url":"https://api.github.com/repos/o/r/pulls/7"}}
            """);
        Assert.IsNull(NotificationsClient.ParseSubject(json.RootElement).PullRequest);
    }

    [TestMethod]
    public void ParseSubject_DeletedForkAndNullMetadata_AreSupported()
    {
        using var json = JsonDocument.Parse("""
            {
              "number":7,"title":"PR","html_url":"https://github.com/o/r/pull/7","state":"open",
              "head":{"ref":"fix","repo":null},"base":{"ref":"main","repo":null},"user":null,
              "labels":[null,{}],"commits":null,"additions":"unknown"
            }
            """);
        var details = new PullRequestDetails(NotificationsClient.ParseSubject(json.RootElement).PullRequest!);
        Assert.AreEqual("fix", Text(details, "From"));
        Assert.AreEqual("main", Text(details, "Into"));
        Assert.IsFalse(details.Metadata.Any(m => m.Key is "Author" or "Repository" or "Labels" or "Commits" or "Additions"));
    }

    [TestMethod]
    public void ParseSubject_InvalidPullRequest_ReportsFailure()
    {
        using var json = JsonDocument.Parse("""{"head":{},"base":{},"number":7,"title":"PR"}""");
        var error = Assert.ThrowsExactly<GitHubApiException>(() => NotificationsClient.ParseSubject(json.RootElement));
        Assert.AreEqual("GitHub sent back a pull request we couldn't read.", error.Message);
    }

    private static string Text(PullRequestDetails details, string key)
    {
        var data = Assert.IsInstanceOfType<IDetailsLink>(details.Metadata.Single(m => m.Key == key).Data);
        return data.Text;
    }
}
