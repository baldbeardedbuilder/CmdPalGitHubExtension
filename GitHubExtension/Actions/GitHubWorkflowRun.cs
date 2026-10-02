// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal sealed record GitHubWorkflowRun(
    long Id,
    string Name,
    string DisplayTitle,
    string Actor,
    string Status,
    string? Conclusion,
    DateTimeOffset CreatedAt,
    Uri WebUrl);

internal sealed record WorkflowRunsPageResult(IReadOnlyList<GitHubWorkflowRun> Runs, Uri? NextPage);
