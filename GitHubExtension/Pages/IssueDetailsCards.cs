// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal static class IssueDetailsCards
{
    public static string Loading() => Card(
        """{ "type": "TextBlock", "text": "Loading issue details...", "wrap": true, "isSubtle": true }""");

    public static string SignedOut() => Card(
        """{ "type": "TextBlock", "text": "Sign in to view issue details.", "wrap": true, "isSubtle": true }""");

    public static string Error(string message) => Card(
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(message)}}, "wrap": true, "color": "Attention" }""",
        $$"""
        { "type": "ActionSet", "actions": [
            { "type": "Action.Submit", "id": "retry", "title": "Try again", "data": { "action": "{{IssueDetailsActions.Retry}}" } }
        ] }
        """);

    public static string Details(string repository, GitHubIssue issue) => Card(
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str($"{StateText(issue.State)}  ·  {repository}#{issue.Number}")}}, "wrap": true, "isSubtle": true, "spacing": "None" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(issue.Title)}}, "wrap": true, "size": "Large", "weight": "Bolder", "spacing": "Small" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(Metadata(issue))}}, "wrap": true, "isSubtle": true, "spacing": "Small" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(Labels(issue))}}, "wrap": true, "spacing": "Medium" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(string.IsNullOrWhiteSpace(issue.Body) ? "No description provided." : issue.Body)}}, "wrap": true, "spacing": "Medium" }""",
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "browser", "title": "Open in browser", "data": { "action": "{{IssueDetailsActions.OpenInBrowser}}" } }
        ] }
        """);

    private static string StateText(SubjectState state) => state switch
    {
        SubjectState.Open => "OPEN",
        SubjectState.Closed => "CLOSED",
        SubjectState.NotPlanned => "NOT PLANNED",
        _ => "ISSUE",
    };

    private static string Metadata(GitHubIssue issue)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(issue.Author))
        {
            details.Add($"opened by @{issue.Author}");
        }

        if (issue.Assignees.Count > 0)
        {
            details.Add($"assigned to {string.Join(", ", issue.Assignees.Select(name => $"@{name}"))}");
        }

        details.Add($"opened {issue.CreatedAt.ToLocalTime().ToString("MMM d, yyyy", System.Globalization.CultureInfo.CurrentCulture)}");
        details.Add($"{issue.Comments} comments");
        return string.Join("  ·  ", details);
    }

    private static string Labels(GitHubIssue issue) =>
        issue.Labels.Count == 0 ? "No labels" : string.Join("  ·  ", issue.Labels);

    private static string Card(params string[] elements) => $$"""
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "body": [ {{string.Join(",\n", elements)}} ]
        }
        """;
}
