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
    string? DetailsError = null);

internal sealed record AgentTasksPageResult(IReadOnlyList<GitHubAgentTask> Tasks, Uri? NextPage);
