// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BaldBeardedBuilder.CmdPal.GitHub.Api;

internal static class GitHubJson
{
    internal static string String(string value) => JsonSerializer.Serialize(value, GitHubJsonContext.Default.String);

    internal static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record CreateCodespaceRequest(
    [property: JsonPropertyName("repository_id")] long RepositoryId,
    [property: JsonPropertyName("ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Ref);

internal sealed record CreateAgentTaskRequest(
    [property: JsonPropertyName("prompt")] string Prompt,
    [property: JsonPropertyName("create_pull_request")] bool CreatePullRequest,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model,
    [property: JsonPropertyName("custom_agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CustomAgent,
    [property: JsonPropertyName("base_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseRef,
    [property: JsonPropertyName("head_ref"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? HeadRef);

internal sealed record GraphQLRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] JsonObject? Variables,
    [property: JsonPropertyName("operationName")] string? OperationName);

internal sealed record PullRequestStateRequest(
    [property: JsonPropertyName("state")] string State);

internal sealed record PullRequestReviewersRequest(
    [property: JsonPropertyName("reviewers")] string[] Reviewers,
    [property: JsonPropertyName("team_reviewers")] string[] TeamReviewers);

internal sealed record GitHubNamesRequest(
    [property: JsonPropertyName("assignees"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Assignees = null,
    [property: JsonPropertyName("labels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Labels = null);

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(CreateCodespaceRequest))]
[JsonSerializable(typeof(CreateAgentTaskRequest))]
[JsonSerializable(typeof(GraphQLRequest))]
[JsonSerializable(typeof(PullRequestStateRequest))]
[JsonSerializable(typeof(PullRequestReviewersRequest))]
[JsonSerializable(typeof(GitHubNamesRequest))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
