// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal static class RerunWorkflowCards
{
    internal static string Confirm(string repository, GitHubWorkflowRun run, string? message = null) => Card(
        Text($"Rerun {repository} / {run.Name} (run {run.Id}, attempt {run.RunAttempt?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "unknown"})?"),
        Text("This uses GitHub Actions compute and may incur charges. Confirm only if you want to run these jobs again."),
        message is null ? string.Empty : Text(message),
        $$"""
        { "type": "Input.ChoiceSet", "id": "jobs", "label": "Jobs to rerun", "value": "all", "choices": [
            { "title": "All jobs", "value": "all" }
            {{(run.CanRerunFailed ? """, { "title": "Failed jobs and their dependents", "value": "failed" }""" : string.Empty)}}
        ] }
        """,
        """{ "type": "Input.Toggle", "id": "debug", "title": "Enable debug logging", "value": "false", "valueOn": "true", "valueOff": "false" }""",
        """{ "type": "ActionSet", "actions": [{ "type": "Action.Submit", "title": "Confirm rerun", "data": { "action": "rerun" } }] }""");

    internal static string Status(string repository, long runId, string message) => Card(
        Text($"{repository} / run {runId}"),
        Text(message),
        Text("A request being accepted does not mean the rerun has finished. Refresh to check the attempt and status."),
        """{ "type": "ActionSet", "actions": [{ "type": "Action.Submit", "title": "Refresh status", "associatedInputs": "none", "data": { "action": "refresh" } }] }""");

    private static string Text(string text) => $$"""{ "type": "TextBlock", "text": {{JsonSerializer.Serialize(text)}}, "wrap": true }""";

    private static string Card(params string[] elements) => $$"""
        { "$schema": "http://adaptivecards.io/schemas/adaptive-card.json", "type": "AdaptiveCard", "version": "1.6",
          "body": [{{string.Join(",\n", elements.Where(e => !string.IsNullOrEmpty(e)))}}] }
        """;
}
