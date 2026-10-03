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

    public static string Details(string repository, GitHubIssue issue, bool mutations = false, string? feedback = null, Uri? authorizeUrl = null, string? login = null) => Card(
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str($"{StateText(issue.State)}  ·  {repository}#{issue.Number}")}}, "wrap": true, "isSubtle": true, "spacing": "None" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(issue.Title)}}, "wrap": true, "size": "Large", "weight": "Bolder", "spacing": "Small" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(Metadata(issue))}}, "wrap": true, "isSubtle": true, "spacing": "Small" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(Labels(issue))}}, "wrap": true, "spacing": "Medium" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(string.IsNullOrWhiteSpace(issue.Body) ? "No description provided." : issue.Body)}}, "wrap": true, "spacing": "Medium" }""",
        $$"""{ "type": "TextBlock", "text": {{SignInCards.Str(feedback ?? string.Empty)}}, "wrap": true }""",
        authorizeUrl is null
            ? """{ "type": "TextBlock", "text": "", "isVisible": false }"""
            : $$"""{ "type": "ActionSet", "actions": [{ "type": "Action.OpenUrl", "title": "Authorize organization access", "url": {{SignInCards.Str(authorizeUrl.AbsoluteUri)}} }] }""",
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            {{Action("Open in browser", IssueDetailsActions.OpenInBrowser)}},
            {{Action("Refresh", IssueDetailsActions.Retry)}}
        ] }
        """,
        MutationActions(issue, mutations, login));

    public static string Picker(IssueChangeKind kind, bool add, IReadOnlyList<string> names) => Card(
        $$"""{ "type": "TextBlock", "text": {{GitHubJson.String($"{(add ? "Add" : "Remove")} {(kind == IssueChangeKind.Assignee ? "assignee" : "label")}")}}, "size": "Large", "wrap": true }""",
        names.Count == 0
            ? """{ "type": "TextBlock", "text": "No available selections.", "wrap": true }"""
            : $$"""
            { "type": "Input.ChoiceSet", "id": "selection", "label": "Choose an existing selection", "style": "compact", "isRequired": true,
              "choices": [{{string.Join(",", names.Select((name, index) => $$"""{"title":{{GitHubJson.String(name)}},"value":"{{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}}"}"""))}}] }
            """,
        $$"""{ "type": "ActionSet", "actions": [{{(names.Count == 0 ? string.Empty : Action("Review change", IssueDetailsActions.Select) + ",")}}{{Action("Cancel", "cancel")}}] }""");

    private static string MutationActions(GitHubIssue issue, bool enabled, string? login)
    {
        if (!enabled)
        {
            return """{ "type": "TextBlock", "text": "", "isVisible": false }""";
        }

        var actions = new List<string>();
        if (issue.State == SubjectState.Open)
        {
            actions.Add(Action("Close as completed", IssueDetailsActions.CloseCompleted));
            actions.Add(Action("Close as not planned", IssueDetailsActions.CloseNotPlanned));
        }
        else if (issue.State is SubjectState.Closed or SubjectState.NotPlanned)
        {
            actions.Add(Action("Reopen issue", IssueDetailsActions.Reopen));
        }

        if (!string.IsNullOrWhiteSpace(login))
        {
            var assigned = issue.Assignees.Contains(login, StringComparer.OrdinalIgnoreCase);
            actions.Add(Action(assigned ? "Remove myself" : "Assign myself", assigned ? IssueDetailsActions.RemoveSelf : IssueDetailsActions.AssignSelf));
        }

        actions.Add(Action("Add assignee", IssueDetailsActions.AddAssignee));
        if (issue.Assignees.Count > 0)
        {
            actions.Add(Action("Remove assignee", IssueDetailsActions.RemoveAssignee));
        }

        actions.Add(Action("Add label", IssueDetailsActions.AddLabel));
        if (issue.Labels.Count > 0)
        {
            actions.Add(Action("Remove label", IssueDetailsActions.RemoveLabel));
        }

        return $$"""{ "type": "Container", "items": [{{string.Join(",", actions.Chunk(3).Select(group => $$"""{ "type": "ActionSet", "actions": [{{string.Join(",", group)}}] }"""))}}] }""";
    }

    private static string Action(string title, string action) =>
        $$"""{ "type": "Action.Submit", "title": {{GitHubJson.String(title)}}, "associatedInputs": "{{(action == IssueDetailsActions.Select ? "auto" : "none")}}", "data": { "action": {{GitHubJson.String(action)}} } }""";

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
