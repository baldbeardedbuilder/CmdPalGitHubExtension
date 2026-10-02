// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// Your GitHub inbox. Loads lazily on first view, fills in issue and PR state as it arrives, and filters as you type.
/// </summary>
internal sealed partial class NotificationsPage : DynamicListPage
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.notifications";

    private const int MaxConcurrentSubjectRequests = 6;

    private readonly AuthService _auth;
    private readonly INotificationsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<NotificationItem> _items = [];
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, SubjectDetails Details)> _subjectCache = [];
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;

    public NotificationsPage(AuthService auth, INotificationsClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        Id = PageId;
        Name = "Open";
        Title = "Notifications";
        Icon = Icons.Notifications;
        PlaceholderText = "Filter notifications...";
        _auth.AccountChanged += (_, _) => Reset();
    }

    /// <summary>
    /// The in flight load, including the issue and PR state lookups. Handy for tests.
    /// </summary>
    internal Task CurrentLoad
    {
        get
        {
            lock (_lock)
            {
                return _currentLoad;
            }
        }
    }

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        NotificationItem[] snapshot;
        string? error;
        lock (_lock)
        {
            needsLoad = !_loaded && !_fetching;
            snapshot = [.. _items];
            error = _error;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        EmptyContent = error is not null
            ? new CommandItem(new RefreshNotificationsCommand(this)) { Title = "Couldn't load notifications", Subtitle = error, Icon = Icons.Notifications }
            : new CommandItem(new NoOpCommand()) { Title = "You're all caught up", Subtitle = "No notifications to show", Icon = Icons.Notifications };

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0
            ? snapshot
            : [.. snapshot.Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override void LoadMore() => StartLoad(reset: false);

    public Task RefreshAsync()
    {
        lock (_lock)
        {
            _generation++;
            _fetching = false;
        }

        return StartLoad(reset: true);
    }

    internal void Open(NotificationItem item)
    {
        _browser.Open(item.WebUrl);
        if (item.Unread)
        {
            MarkAsRead(item);
        }
    }

    internal void MarkAsRead(NotificationItem item)
    {
        if (_auth.CurrentAccount is not { } account)
        {
            return;
        }

        item.SetUnread(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.MarkAsReadAsync(account, item.Notification.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GitHubApiException)
            {
                item.SetUnread(true);
            }
        });
    }

    internal void MarkAsDone(NotificationItem item)
    {
        if (_auth.CurrentAccount is not { } account)
        {
            return;
        }

        lock (_lock)
        {
            _items.Remove(item);
        }

        RaiseItemsChanged();
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.MarkAsDoneAsync(account, item.Notification.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (GitHubApiException)
            {
                await RefreshAsync().ConfigureAwait(false);
            }
        });
    }

    private Task StartLoad(bool reset)
    {
        GitHubAccount? account;
        Uri? page;
        int generation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            if (account is null || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _fetching = true;
            page = reset ? null : _nextPage;
            generation = _generation;
            _currentLoad = Task.Run(() => LoadAsync(account, page, reset, generation));
        }

        IsLoading = true;
        return _currentLoad;
    }

    private async Task LoadAsync(GitHubAccount account, Uri? page, bool reset, int generation)
    {
        List<NotificationItem> added = [];
        try
        {
            var result = await _client.GetNotificationsAsync(account, page, CancellationToken.None).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                if (reset)
                {
                    _items.Clear();
                }

                var known = _items.Select(i => i.Notification.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var notification in result.Notifications.Where(n => known.Add(n.Id)))
                {
                    var item = new NotificationItem(this, notification, NotificationFormatting.WebUrl(account.Host, notification), now);
                    if (_subjectCache.TryGetValue(notification.Id, out var cached) && cached.UpdatedAt == notification.UpdatedAt)
                    {
                        item.ApplySubject(cached.Details);
                    }

                    _items.Add(item);
                    added.Add(item);
                }

                _nextPage = result.NextPage;
                _loaded = true;
                _error = null;
            }

            HasMoreItems = result.NextPage is not null;
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                _error = ex.Message;
                _loaded = true;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _fetching = false;
                }
            }

            IsLoading = false;
            RaiseItemsChanged();
        }

        await LoadSubjectsAsync(account, added).ConfigureAwait(false);
    }

    private async Task LoadSubjectsAsync(GitHubAccount account, List<NotificationItem> items)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentSubjectRequests);
        await Task.WhenAll(items
            .Where(i => i.Subject is null && NotificationFormatting.HasState(i.Notification))
            .Select(async item =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (await _client.GetSubjectAsync(account, item.Notification.SubjectApiUrl!, CancellationToken.None).ConfigureAwait(false) is { } details)
                    {
                        item.ApplySubject(details);
                        lock (_lock)
                        {
                            _subjectCache[item.Notification.Id] = (item.Notification.UpdatedAt, details);
                        }
                    }
                }
                catch (GitHubApiException)
                {
                    // A missing badge isn't worth interrupting anyone over.
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);
    }

    private void Reset()
    {
        lock (_lock)
        {
            _generation++;
            _items.Clear();
            _subjectCache.Clear();
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }
}
