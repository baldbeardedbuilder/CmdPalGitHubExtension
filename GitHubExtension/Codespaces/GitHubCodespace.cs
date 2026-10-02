// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

internal sealed record GitHubCodespace(
    string Name,
    string? DisplayName,
    string RepositoryFullName,
    string? Branch,
    string State,
    DateTimeOffset LastUsedAt,
    Uri WebUrl);

internal sealed record CodespacesPageResult(IReadOnlyList<GitHubCodespace> Codespaces, Uri? NextPage);
