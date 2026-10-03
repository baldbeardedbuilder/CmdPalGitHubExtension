// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

const string escaped = "owner/quote\"\\line\n\t\u263a";
Check(!JsonSerializer.IsReflectionEnabledByDefault, "Reflection serialization must be disabled.");
using (var text = JsonDocument.Parse(GitHubJson.String(escaped)))
{
    Equal(escaped, text.RootElement.GetString(), "String escaping");
}

foreach (var branch in new string?[] { null, "", " \t ", $" {escaped} " })
{
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(
        new CreateCodespaceRequest(long.MaxValue, GitHubJson.Optional(branch)),
        GitHubJsonContext.Default.CreateCodespaceRequest));
    var root = json.RootElement;
    Check(root.GetProperty("repository_id").GetInt64() == long.MaxValue, "Repository ID must remain a 64-bit JSON number.");
    var hasRef = root.TryGetProperty("ref", out var reference);
    Check(hasRef == !string.IsNullOrWhiteSpace(branch), "Blank branch must omit ref.");
    Check(root.EnumerateObject().Count() == (hasRef ? 2 : 1), "Codespace request fields");
    if (hasRef) Equal(escaped, reference.GetString(), "Branch escaping and trimming");
}

using (var form = Card(CreateCodespaceCards.Form(escaped, escaped, escaped)))
{
    var body = form.RootElement.GetProperty("body");
    Equal(escaped, Input(body, "repository").GetProperty("value").GetString(), "Repository input");
    Equal(escaped, Input(body, "branch").GetProperty("value").GetString(), "Branch input");
    Equal(escaped, body[4].GetProperty("text").GetString(), "Error text");
    Check(Actions(body).Single().GetProperty("data").GetProperty("action").GetString() == CreateCodespaceActions.Create,
        "Create action payload");
}
using (var form = Card(CreateCodespaceCards.Form(null, null, null)))
{
    var body = form.RootElement.GetProperty("body");
    Check(body.GetArrayLength() == 5, "Form without error");
    Equal("", Input(body, "repository").GetProperty("value").GetString(), "Empty repository");
    Equal("", Input(body, "branch").GetProperty("value").GetString(), "Empty branch");
}
using (var creating = Card(CreateCodespaceCards.Creating(escaped)))
{
    Equal(escaped, creating.RootElement.GetProperty("body")[1].GetProperty("text").GetString(), "Creating repository");
}
var space = new GitHubCodespace("test", null, escaped, null, "Queued", DateTimeOffset.MinValue, new Uri("https://test.github.dev"));
using (var created = Card(CreateCodespaceCards.Created(space)))
{
    var body = created.RootElement.GetProperty("body");
    Equal($"GitHub is preparing your development environment for {escaped}.", body[1].GetProperty("text").GetString(), "Created repository");
    var actions = Actions(body).ToArray();
    Check(actions.Length == 2, "Created action count");
    Equal(CreateCodespaceActions.Open, actions[0].GetProperty("data").GetProperty("action").GetString(), "Open action");
    Equal(CreateCodespaceActions.CreateAnother, actions[1].GetProperty("data").GetProperty("action").GetString(), "Another action");
    Equal("none", actions[1].GetProperty("associatedInputs").GetString(), "Another action inputs");
}

foreach (var optional in new string?[] { null, "", " \t ", $" {escaped} " })
{
    var value = GitHubJson.Optional(optional);
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(
        new CreateAgentTaskRequest(escaped, false, value, value, value, value),
        GitHubJsonContext.Default.CreateAgentTaskRequest));
    var root = json.RootElement;
    Equal(escaped, root.GetProperty("prompt").GetString(), "Agent prompt");
    Check(!root.GetProperty("create_pull_request").GetBoolean(), "False must not be omitted.");
    foreach (var name in new[] { "model", "custom_agent", "base_ref", "head_ref" })
    {
        Check(root.TryGetProperty(name, out var property) == (value is not null), $"Agent option {name}");
        if (value is not null) Equal(escaped, property.GetString(), $"Agent option escaping: {name}");
    }
    Check(root.EnumerateObject().Count() == (value is null ? 2 : 6), "Agent request fields");
}
var draft = new AgentTaskRequest(escaped, escaped, escaped, escaped, escaped, true);
using (var form = Card(CreateAgentTaskCards.Form(escaped, draft, escaped)))
{
    var body = form.RootElement.GetProperty("body");
    foreach (var id in new[] { "prompt", "model", "customAgent", "baseRef", "headRef" })
        Equal(escaped, Input(body, id).GetProperty("value").GetString(), $"Agent input {id}");
}
using (var review = Card(CreateAgentTaskCards.Review(escaped, draft, true)))
    Equal(escaped, review.RootElement.GetProperty("body")[2].GetProperty("text").GetString(), "Review prompt");
using (var starting = Card(CreateAgentTaskCards.Starting(escaped)))
    Equal(escaped, starting.RootElement.GetProperty("body")[1].GetProperty("text").GetString(), "Starting repository");
var task = new GitHubAgentTask(escaped, escaped, new Uri("https://github.com/copilot/tasks/test"), "queued", DateTimeOffset.MinValue, null);
using (var started = Card(CreateAgentTaskCards.Started(task)))
    Equal(escaped, started.RootElement.GetProperty("body")[1].GetProperty("text").GetString(), "Started task title");
using (var unknown = Card(CreateAgentTaskCards.OutcomeUnknown(escaped, new Uri("https://github.com/o/r"), [task], false)))
    Equal(escaped, unknown.RootElement.GetProperty("body")[2].GetProperty("text").GetString(), "Unknown outcome repository");

using (var json = JsonDocument.Parse(JsonSerializer.Serialize(
    new GraphQLRequest(escaped, new JsonObject
    {
        ["id"] = escaped, ["number"] = 7, ["enabled"] = true,
        ["nested"] = new JsonObject { ["items"] = new JsonArray(escaped, 2, false, null) },
    }, escaped), GitHubJsonContext.Default.GraphQLRequest)))
{
    var root = json.RootElement;
    Equal(escaped, root.GetProperty("query").GetString(), "GraphQL query");
    Equal(escaped, root.GetProperty("operationName").GetString(), "GraphQL operation");
    var variables = root.GetProperty("variables");
    Equal(escaped, variables.GetProperty("id").GetString(), "GraphQL variable escaping");
    Check(variables.GetProperty("number").GetInt32() == 7, "GraphQL numeric variable");
    Check(variables.GetProperty("enabled").GetBoolean(), "GraphQL boolean variable");
    var items = variables.GetProperty("nested").GetProperty("items");
    Equal(escaped, items[0].GetString(), "GraphQL nested array escaping");
    Check(items[1].GetInt32() == 2 && !items[2].GetBoolean() && items[3].ValueKind == JsonValueKind.Null,
        "GraphQL nested array types");
}
using (var json = JsonDocument.Parse(JsonSerializer.Serialize(
    new GraphQLRequest("query", null, null), GitHubJsonContext.Default.GraphQLRequest)))
{
    Check(json.RootElement.GetProperty("variables").ValueKind == JsonValueKind.Null, "Null GraphQL variables");
    Check(json.RootElement.GetProperty("operationName").ValueKind == JsonValueKind.Null, "Null GraphQL operation");
}
var run = new GitHubWorkflowRun(7, escaped, escaped, "actor", "completed", "failure", DateTimeOffset.MinValue,
    new Uri("https://github.com/o/r/actions/runs/7"), RunAttempt: 2);
using (var confirm = Card(RerunWorkflowCards.Confirm(escaped, run, escaped)))
{
    var body = confirm.RootElement.GetProperty("body");
    Equal(escaped, body[2].GetProperty("text").GetString(), "Workflow message");
    Check(Input(body, "jobs").GetProperty("choices").GetArrayLength() == 2, "Failed workflow choices");
}
using (var status = Card(RerunWorkflowCards.Status(escaped, 7, escaped)))
    Equal(escaped, status.RootElement.GetProperty("body")[1].GetProperty("text").GetString(), "Workflow status");

Console.WriteLine("Native JSON smoke checks passed (reflection disabled, no network requests).");

static JsonDocument Card(string json)
{
    var document = JsonDocument.Parse(json);
    Equal("AdaptiveCard", document.RootElement.GetProperty("type").GetString(), "Card type");
    Equal("1.6", document.RootElement.GetProperty("version").GetString(), "Card version");
    return document;
}

static JsonElement Input(JsonElement body, string id) =>
    body.EnumerateArray().Single(element => element.TryGetProperty("id", out var value) && value.GetString() == id);

static IEnumerable<JsonElement> Actions(JsonElement body) =>
    body.EnumerateArray().Where(element => element.GetProperty("type").GetString() == "ActionSet")
        .SelectMany(element => element.GetProperty("actions").EnumerateArray());

static void Equal(string expected, string? actual, string name) => Check(expected == actual, name);

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"JSON smoke check failed: {name}");
}
