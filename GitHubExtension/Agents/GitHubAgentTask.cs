// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Agents;

internal sealed record GitHubAgentTask(
    string Id,
    string Title,
    Uri WebUrl,
    string State,
    DateTimeOffset UpdatedAt,
    long? RepositoryId,
    string? RepositoryFullName = null,
    string? Model = null,
    string? DetailsError = null,
    string? RepositoryError = null,
    IReadOnlyList<AgentSession>? Sessions = null,
    IReadOnlyList<AgentArtifact>? Artifacts = null,
    DateTimeOffset? ArchivedAt = null);

internal sealed record AgentSession(string Id, string? Name, string State, string? Prompt, string? Model,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? HeadRef, string? BaseRef,
    string? Error, string? UsageType, double? UsageAmount);

internal sealed record AgentArtifact(string Provider, string Type, long? Id, string? GlobalId, string? HeadRef, string? BaseRef,
    Uri? WebUrl = null);

internal sealed record AgentQuery(bool Archived = false, string? State = null, string? Repository = null)
{
    internal static readonly string[] States = ["queued", "in_progress", "completed", "failed", "idle", "waiting_for_user", "timed_out", "cancelled"];
}

internal sealed record AgentTasksPageResult(IReadOnlyList<GitHubAgentTask> Tasks, Uri? NextPage);

internal sealed record AgentTaskRequest(
    string Prompt,
    string? Model,
    string? CustomAgent,
    string? BaseRef,
    string? HeadRef,
    bool CreatePullRequest);

internal sealed class AgentTaskOutcomeUnknownException(string message, Exception? innerException = null) : Exception(message, innerException);
