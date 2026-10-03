// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class NotificationsPage
{
    internal PullRequestActionsPage? PullRequestActionsPage(NotificationItem item)
    {
        lock (_lock)
        {
            if (_load.Disposed || _pullRequestActionsClient is null || item.Account is null
                || !ReferenceEquals(item.Account, CurrentAccount) || item.AccountGeneration != _accountGeneration
                || item.Subject?.PullRequest is not { } pullRequest)
            {
                return null;
            }

            if (!_pullRequestActionPages.TryGetValue(item.Notification.Id, out var page))
            {
                page = new PullRequestActionsPage(
                    _auth,
                    _pullRequestActionsClient,
                    item.Account,
                    item.Notification.RepositoryFullName,
                    pullRequest.Number,
                    pullRequest.WebUrl,
                    () => IsCurrentPullRequestNotification(item),
                    RefreshAsync);
                _pullRequestActionPages[item.Notification.Id] = page;
            }

            return page;
        }
    }

    private bool IsCurrentPullRequestNotification(NotificationItem item)
    {
        lock (_lock)
        {
            return !_load.Disposed && item.Account is not null && ReferenceEquals(item.Account, CurrentAccount)
                && item.AccountGeneration == _accountGeneration && _items.Contains(item);
        }
    }

    private PullRequestActionsPage[] TakePullRequestActionPages()
    {
        var pages = _pullRequestActionPages.Values.ToArray();
        _pullRequestActionPages.Clear();
        return pages;
    }
}
