// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

/// <summary>
/// The parts of a GitHub repository we show in a list row.
/// </summary>
internal sealed record GitHubRepository(
    string FullName,
    Uri WebUrl,
    string? Description,
    bool Private,
    bool Fork,
    bool Archived,
    string? Language,
    int Stars,
    int Forks,
    DateTimeOffset PushedAt,
    Uri? CloneUrl);

internal sealed record RepositoriesPageResult(IReadOnlyList<GitHubRepository> Repositories, Uri? NextPage);
