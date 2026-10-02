// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal static class CreateCodespaceCards
{
    public static string Form(string? repository, string? branch, string? error) => Card(
        """{ "type": "TextBlock", "text": "Create a Codespace", "size": "Large", "weight": "Bolder", "wrap": true }""",
        """{ "type": "TextBlock", "text": "Enter a repository you can access as owner/name. Leave the branch blank to use its default branch.", "wrap": true, "isSubtle": true }""",
        $$"""{ "type": "Input.Text", "id": "repository", "label": "Repository", "placeholder": "microsoft/PowerToys", "isRequired": true, "errorMessage": "Enter a repository as owner/name", "value": {{Str(repository ?? string.Empty)}} }""",
        $$"""{ "type": "Input.Text", "id": "branch", "label": "Branch (optional)", "placeholder": "main", "value": {{Str(branch ?? string.Empty)}} }""",
        Error(error),
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "create", "title": "Create Codespace", "style": "positive", "data": { "action": "{{CreateCodespaceActions.Create}}" } }
        ] }
        """);

    public static string Creating(string repository) => Card(
        """{ "type": "TextBlock", "text": "Creating your Codespace...", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$""" { "type": "TextBlock", "text": {{Str(repository)}}, "wrap": true, "isSubtle": true } """);

    public static string Created(GitHubCodespace codespace) => Card(
        """{ "type": "TextBlock", "text": "Codespace created", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$"""{"type": "TextBlock", "text": {{Str($"GitHub is preparing your development environment for {codespace.RepositoryFullName}.")}}, "wrap": true, "isSubtle": true}""",
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "open", "title": "Open Codespace", "style": "positive", "data": { "action": "{{CreateCodespaceActions.Open}}" } },
            { "type": "Action.Submit", "id": "another", "title": "Create another", "associatedInputs": "none", "data": { "action": "{{CreateCodespaceActions.CreateAnother}}" } }
        ] }
        """);

    internal static string Str(string value) => JsonSerializer.Serialize(value);

    private static string Error(string? error) => string.IsNullOrEmpty(error)
        ? string.Empty
        : $$"""{ "type": "TextBlock", "text": {{Str(error)}}, "wrap": true, "color": "Attention" }""";

    private static string Card(params string[] elements) => $$"""
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "body": [ {{string.Join(",\n", elements.Where(e => !string.IsNullOrEmpty(e)))}} ]
        }
        """;
}

internal static class CreateCodespaceActions
{
    public const string Create = "create";
    public const string Open = "open";
    public const string CreateAnother = "createAnother";
}
