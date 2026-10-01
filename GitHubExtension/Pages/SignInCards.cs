// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// Adaptive card templates for <see cref="SignInPage"/>. Every dynamic value goes through <see cref="Str"/> so it's valid JSON.
/// </summary>
internal static class SignInCards
{
    private const string PatDocsUrl = "https://docs.github.com/enterprise-server@latest/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens";

    public static string Start(string logo, string? error) => Card(
        Logo(logo),
        $$"""{ "type": "TextBlock", "text": {{Str(SignInPage.Message)}}, "wrap": true, "horizontalAlignment": "Center", "spacing": "Large" }""",
        Error(error),
        $$"""
        { "type": "ActionSet", "horizontalAlignment": "Center", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "github", "title": "Sign in with GitHub", "style": "positive", "data": { "action": "{{SignInActions.GitHub}}" } }
        ] }
        """,
        $$"""
        { "type": "ActionSet", "horizontalAlignment": "Center", "actions": [
            { "type": "Action.Submit", "id": "showEnterprise", "title": "Sign in with GitHub Enterprise account", "data": { "action": "{{SignInActions.ShowEnterprise}}" } }
        ] }
        """);

    public static string Waiting(string logo, string message) => Card(
        Logo(logo),
        $$"""{ "type": "TextBlock", "text": {{Str(message)}}, "wrap": true, "horizontalAlignment": "Center", "spacing": "Large" }""",
        $$"""
        { "type": "ActionSet", "horizontalAlignment": "Center", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "cancel", "title": "Cancel", "data": { "action": "{{SignInActions.Cancel}}" } }
        ] }
        """);

    public static string SignedIn(string logo, GitHubAccount account) => Card(
        Logo(logo),
        $$"""{ "type": "TextBlock", "text": {{Str($"You're signed in as @{account.Login}")}}, "wrap": true, "horizontalAlignment": "Center", "size": "Large", "weight": "Bolder", "spacing": "Large" }""",
        $$"""{ "type": "TextBlock", "text": {{Str(account.Host.Name)}}, "wrap": true, "horizontalAlignment": "Center", "isSubtle": true, "spacing": "Small" }""",
        $$"""
        { "type": "ActionSet", "horizontalAlignment": "Center", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "done", "title": "Let's go", "style": "positive", "data": { "action": "{{SignInActions.Done}}" } }
        ] }
        """);

    public static string Enterprise(string? error, string? serverUrl) => Card(
        """{ "type": "TextBlock", "text": "Sign in to GitHub Enterprise", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str($"Enter your server URL and a [personal access token]({PatDocsUrl}). Give the token the repo, read:org, notifications, and codespace scopes.")}}, "wrap": true, "isSubtle": true }""",
        $$"""{ "type": "Input.Text", "id": "serverUrl", "label": "Server URL", "placeholder": "https://github.example.com", "isRequired": true, "errorMessage": "Enter your server URL", "value": {{Str(serverUrl ?? string.Empty)}} }""",
        """{ "type": "Input.Text", "id": "token", "label": "Personal access token", "style": "Password", "isRequired": true, "errorMessage": "Enter a personal access token" }""",
        Error(error),
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "enterprise", "title": "Sign in", "style": "positive", "data": { "action": "{{SignInActions.Enterprise}}" } },
            { "type": "Action.Submit", "id": "back", "title": "Back", "associatedInputs": "none", "data": { "action": "{{SignInActions.Back}}" } }
        ] }
        """);

    internal static string Str(string value) => JsonSerializer.Serialize(value, SignInCardsJsonContext.Default.String);

    private static string Logo(string logo) => string.IsNullOrEmpty(logo)
        ? string.Empty
        : $$"""{ "type": "Image", "url": {{Str(logo)}}, "altText": "GitHub", "horizontalAlignment": "Center", "width": "64px", "height": "64px" }""";

    private static string Error(string? error) => string.IsNullOrEmpty(error)
        ? string.Empty
        : $$"""{ "type": "TextBlock", "text": {{Str(error)}}, "wrap": true, "color": "Attention", "horizontalAlignment": "Center" }""";

    private static string Card(params string[] elements) => $$"""
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "verticalContentAlignment": "Center",
            "body": [ {{string.Join(",\n", elements.Where(e => !string.IsNullOrEmpty(e)))}} ]
        }
        """;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class SignInCardsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
