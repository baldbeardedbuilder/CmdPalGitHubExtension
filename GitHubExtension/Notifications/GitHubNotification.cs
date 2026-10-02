// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

/// <summary>
/// A notification thread from the GitHub notifications API.
/// </summary>
internal sealed record GitHubNotification(
    string Id,
    string Title,
    string SubjectType,
    Uri? SubjectApiUrl,
    string RepositoryFullName,
    Uri? RepositoryWebUrl,
    string Reason,
    bool Unread,
    DateTimeOffset UpdatedAt);

internal enum SubjectState
{
    Unknown,
    Open,
    Draft,
    Merged,
    Closed,
    NotPlanned,
}

/// <summary>
/// The state badge, browser link, and issue or pull request details from the subject lookup.
/// </summary>
internal sealed record SubjectDetails(
    SubjectState State,
    Uri? WebUrl,
    GitHubPullRequest? PullRequest = null,
    GitHubIssue? Issue = null);

internal sealed record NotificationsPageResult(IReadOnlyList<GitHubNotification> Notifications, Uri? NextPage);
