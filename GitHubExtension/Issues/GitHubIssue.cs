// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal sealed record GitHubIssue(
    int Number,
    string Title,
    string? Body,
    SubjectState State,
    Uri WebUrl,
    DateTimeOffset CreatedAt,
    string? Author,
    IReadOnlyList<string> Assignees,
    IReadOnlyList<string> Labels,
    int Comments,
    int? MilestoneNumber = null);
