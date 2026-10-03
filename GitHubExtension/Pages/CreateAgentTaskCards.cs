// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal static class CreateAgentTaskCards
{
    public static string Form(string repository, AgentTaskRequest? draft, string? error) => Card(
        """{ "type": "TextBlock", "text": "Start a Copilot task", "size": "Large", "weight": "Bolder", "wrap": true }""",
        """{ "type": "TextBlock", "text": "Describe the work for Copilot. Model and custom agent names depend on your plan and organization policy.", "wrap": true, "isSubtle": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(repository)}}, "wrap": true, "isSubtle": true }""",
        $$"""{ "type": "Input.Text", "id": "prompt", "label": "Prompt", "isMultiline": true, "isRequired": true, "errorMessage": "Enter a prompt", "value": {{Str(draft?.Prompt ?? string.Empty)}} }""",
        $$"""{ "type": "Input.Text", "id": "model", "label": "Model (optional)", "placeholder": "Use your default model", "value": {{Str(draft?.Model ?? string.Empty)}} }""",
        $$"""{ "type": "Input.Text", "id": "customAgent", "label": "Custom agent (optional)", "placeholder": "Filename without extension", "value": {{Str(draft?.CustomAgent ?? string.Empty)}} }""",
        $$"""{ "type": "Input.Text", "id": "baseRef", "label": "Base branch (optional)", "placeholder": "main", "value": {{Str(draft?.BaseRef ?? string.Empty)}} }""",
        $$"""{ "type": "Input.Toggle", "id": "createPullRequest", "title": "Create a pull request", "value": {{(draft?.CreatePullRequest == true ? "\"true\"" : "\"false\"")}} }""",
        $$"""{ "type": "Input.Text", "id": "headRef", "label": "Head branch (optional)", "placeholder": "Existing branch", "value": {{Str(draft?.HeadRef ?? string.Empty)}} }""",
        Error(error),
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "review", "title": "Review task", "style": "positive", "data": { "action": "{{CreateAgentTaskActions.Review}}" } }
        ] }
        """);

    public static string Review(string repository, AgentTaskRequest draft, bool previousOutcomeUnknown) => Card(
        """{ "type": "TextBlock", "text": "Review before starting", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(repository)}}, "wrap": true, "isSubtle": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(draft.Prompt)}}, "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(Summary(draft))}}, "wrap": true, "isSubtle": true }""",
        previousOutcomeUnknown
            ? """{ "type": "TextBlock", "text": "A previous submission may still be running. Check repository tasks before creating another one.", "wrap": true, "color": "Attention" }"""
            : string.Empty,
        """{ "type": "TextBlock", "text": "Starting this task uses Copilot cloud agent compute and may use your plan's premium requests or AI credits.", "wrap": true, "color": "Attention" }""",
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "back", "title": "Edit", "data": { "action": "{{CreateAgentTaskActions.Back}}" } },
            { "type": "Action.Submit", "id": "start", "title": "{{(previousOutcomeUnknown ? "Start another task" : "Start task")}}", "style": "positive", "data": { "action": "{{CreateAgentTaskActions.Start}}" } }
        ] }
        """);

    public static string Starting(string repository) => Card(
        """{ "type": "TextBlock", "text": "Starting your Copilot task...", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(repository)}}, "wrap": true, "isSubtle": true }""");

    public static string Started(GitHubAgentTask task) => Card(
        """{ "type": "TextBlock", "text": "Copilot task started", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(task.Title)}}, "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(task.Id)}}, "wrap": true, "isSubtle": true }""",
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.OpenUrl", "title": "Open task on GitHub", "url": {{Str(task.WebUrl.AbsoluteUri)}} }
        ] }
        """);

    public static string OutcomeUnknown(
        string repository,
        Uri repositoryUrl,
        IReadOnlyList<GitHubAgentTask>? candidates,
        bool couldNotCheck) => Card(
        """{ "type": "TextBlock", "text": "Task outcome needs checking", "size": "Large", "weight": "Bolder", "wrap": true }""",
        $$"""{ "type": "TextBlock", "text": {{Str(couldNotCheck
            ? "GitHub's response was lost and the task list couldn't be checked. Don't submit again until you check the repository's tasks."
            : candidates is { Count: > 0 }
                ? "GitHub's response was lost. These new tasks appeared in the repository; check them before submitting again."
                : "GitHub's response was lost and no new task is visible yet. It may still be processing, so check again before submitting.")}}, "wrap": true, "color": "Attention" }""",
        $$"""{ "type": "TextBlock", "text": {{Str(repository)}}, "wrap": true, "isSubtle": true }""",
        candidates is { Count: > 0 } ? CandidateLinks(candidates) : string.Empty,
        $$"""
        { "type": "ActionSet", "spacing": "Large", "actions": [
            { "type": "Action.Submit", "id": "check", "title": "Check again", "data": { "action": "{{CreateAgentTaskActions.Check}}" } },
            { "type": "Action.Submit", "id": "edit", "title": "Edit draft", "data": { "action": "{{CreateAgentTaskActions.Edit}}" } },
            { "type": "Action.OpenUrl", "title": "Open repository tasks", "url": {{Str(new Uri(repositoryUrl.AbsoluteUri.TrimEnd('/') + "/agents").AbsoluteUri)}} }
        ] }
        """);

    internal static string Str(string value) => JsonSerializer.Serialize(value);

    private static string CandidateLinks(IReadOnlyList<GitHubAgentTask> candidates) => $$"""
        { "type": "Container", "items": [
            {{string.Join(",\n", candidates.Select(task => $$"""
                { "type": "ActionSet", "actions": [{ "type": "Action.OpenUrl", "title": {{Str($"{task.Title} ({task.Id})")}}, "url": {{Str(task.WebUrl.AbsoluteUri)}} }] }
                """))}}
        ] }
        """;

    private static string Summary(AgentTaskRequest draft)
    {
        var options = new List<string>();
        Add("Model", draft.Model);
        Add("Custom agent", draft.CustomAgent);
        Add("Base branch", draft.BaseRef);
        Add("Head branch", draft.HeadRef);
        options.Add(draft.CreatePullRequest ? "Create a pull request" : "No pull request");
        return string.Join(" · ", options);

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                options.Add($"{label}: {value}");
            }
        }
    }

    private static string Error(string? error) => string.IsNullOrEmpty(error)
        ? string.Empty
        : $$"""{ "type": "TextBlock", "text": {{Str(error)}}, "wrap": true, "color": "Attention" }""";

    private static string Card(params string[] elements) => $$"""
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "body": [ {{string.Join(",\n", elements.Where(element => !string.IsNullOrEmpty(element)))}} ]
        }
        """;
}

internal static class CreateAgentTaskActions
{
    public const string Review = "review";
    public const string Back = "back";
    public const string Start = "start";
    public const string Check = "check";
    public const string Edit = "edit";
}
