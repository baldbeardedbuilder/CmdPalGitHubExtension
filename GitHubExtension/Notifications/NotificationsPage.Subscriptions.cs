// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class NotificationsPage
{
    internal Task RefreshAfterIssueMutationAsync(NotificationItem item)
    {
        lock (_lock)
        {
            if (!CanManageSubscription(item))
            {
                return Task.CompletedTask;
            }

            _subjectCache.Remove(item.Notification.Id);
            _load.Invalidate();
        }

        return RefreshAfterMutationAsync();
    }

    private readonly IThreadSubscriptionsClient? _subscriptionsClient;
    private readonly Dictionary<string, WeakReference<ThreadSubscriptionPage>> _subscriptionPages = [];

    internal ThreadSubscriptionPage? SubscriptionPage(NotificationItem item)
    {
        lock (_lock)
        {
            if (_subscriptionsClient is null || !CanManageSubscription(item))
            {
                return null;
            }

            foreach (var key in _subscriptionPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
            {
                _subscriptionPages.Remove(key);
            }

            if (!_subscriptionPages.TryGetValue(item.Notification.Id, out var reference) || !reference.TryGetTarget(out var page))
            {
                page = new ThreadSubscriptionPage(_subscriptionsClient, _executor, item.Account!, item.Notification.Id,
                    () => CanManageSubscription(item), _browser);
                _subscriptionPages[item.Notification.Id] = new(page);
            }

            return page;
        }
    }

    private bool CanManageSubscription(NotificationItem item)
    {
        lock (_lock)
        {
            return !_load.Disposed && item.Account is not null && ReferenceEquals(item.Account, CurrentAccount)
                && item.AccountGeneration == _accountGeneration;
        }
    }

    private ThreadSubscriptionPage[] TakeSubscriptionPages()
    {
        var pages = _subscriptionPages.Values.Select(reference => reference.TryGetTarget(out var page) ? page : null)
            .OfType<ThreadSubscriptionPage>().ToArray();
        _subscriptionPages.Clear();
        return pages;
    }
}
