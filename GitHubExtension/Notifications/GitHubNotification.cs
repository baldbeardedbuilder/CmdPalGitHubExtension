// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

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
/// The bits of an issue or pull request we need to draw a state badge and open it in the browser.
/// </summary>
internal sealed record SubjectDetails(SubjectState State, Uri? WebUrl);

internal sealed record NotificationsPageResult(IReadOnlyList<GitHubNotification> Notifications, Uri? NextPage);
