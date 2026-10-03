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
internal sealed partial class NotificationsPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.notifications";

    private const int MaxConcurrentSubjectRequests = 6;

    private readonly AuthService _auth;
    private readonly INotificationsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly IssueDetailsPage? _issueDetails;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly Lock _lock = new();
    private readonly List<NotificationItem> _items = [];
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, SubjectDetails Details)> _subjectCache = [];
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private volatile bool _disposed;
    private string? _error;
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource _accountLifetime = new();
    private Task _currentLoad = Task.CompletedTask;

    public NotificationsPage(AuthService auth, INotificationsClient client, IBrowserLauncher browser, TimeProvider? time = null, IssueDetailsPage? issueDetails = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _issueDetails = issueDetails;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Notifications, new RefreshNotificationsCommand(this));
        Id = PageId;
        Name = "Open";
        Title = "Notifications";
        Icon = Icons.Notifications;
        PlaceholderText = "Filter notifications...";
        ShowDetails = true;
        _auth.AccountChanged += OnAccountChanged;
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
            ? _emptyContent.Get("Couldn't load notifications", error, refresh: true)
            : _emptyContent.Get("You're all caught up", "No notifications to show");

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
            CancelLoad();
        }

        return StartLoad(reset: true);
    }

    internal ICommandResult Open(NotificationItem item)
    {
        if (item.Unread)
        {
            MarkAsRead(item);
        }

        if (item.Notification.SubjectType == "Issue"
            && item.Notification.SubjectApiUrl is { } issueApiUrl
            && _auth.CurrentAccount is { } account
            && _issueDetails is { } details)
        {
            return details.Open(account, issueApiUrl, item.Notification.RepositoryFullName);
        }

        _browser.Open(item.WebUrl);
        return CommandResult.Dismiss();
    }

    internal void MarkAsRead(NotificationItem item)
    {
        GitHubAccount account;
        CancellationToken token;
        int generation;
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount is not { } currentAccount)
            {
                return;
            }

            account = currentAccount;
            token = _accountLifetime.Token;
            generation = _generation;
        }

        if (token.IsCancellationRequested || _disposed)
        {
            return;
        }

        item.SetUnread(false);
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.MarkAsReadAsync(account, item.Notification.Id, token).ConfigureAwait(false);
            }
            catch (GitHubApiException)
            {
                lock (_lock)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }
                }

                if (token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                item.SetUnread(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        });
    }

    internal void MarkAsDone(NotificationItem item)
    {
        GitHubAccount account;
        CancellationToken token;
        int generation;
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount is not { } currentAccount)
            {
                return;
            }

            account = currentAccount;
            token = _accountLifetime.Token;
            generation = _generation;
        }

        lock (_lock)
        {
            if (_disposed || token.IsCancellationRequested)
            {
                return;
            }

            _items.Remove(item);
        }

        if (_disposed || token.IsCancellationRequested)
        {
            return;
        }

        RaiseItemsChanged();
        _ = Task.Run(async () =>
        {
            try
            {
                await _client.MarkAsDoneAsync(account, item.Notification.Id, token).ConfigureAwait(false);
            }
            catch (GitHubApiException)
            {
                lock (_lock)
                {
                    if (_disposed || generation != _generation)
                    {
                        return;
                    }
                }

                await RefreshAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
        });
    }

    private Task StartLoad(bool reset)
    {
        GitHubAccount? account;
        Uri? page;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            if (_disposed || account is null || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_accountLifetime.Token);
            token = _loadCts.Token;
            _fetching = true;
            page = reset ? null : _nextPage;
            generation = _generation;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation || _disposed)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, page, reset, generation, token));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, Uri? page, bool reset, int generation, CancellationToken token)
    {
        List<NotificationItem> added = [];
        try
        {
            var result = await _client.GetNotificationsAsync(account, page, token).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested || _disposed)
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
                    var item = new NotificationItem(this, notification, NotificationFormatting.WebUrl(account.Host, notification), _browser, now);
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (generation != _generation || _disposed)
                {
                    return;
                }

                _error = ex.Message;
                _loaded = true;
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                publish = generation == _generation && !_disposed;
                if (publish)
                {
                    _fetching = false;
                }
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }

        await LoadSubjectsAsync(account, added, generation, token).ConfigureAwait(false);
    }

    private async Task LoadSubjectsAsync(GitHubAccount account, List<NotificationItem> items, int generation, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        using var throttle = new SemaphoreSlim(MaxConcurrentSubjectRequests);
        await Task.WhenAll(items
            .Where(i => i.Subject is null && NotificationFormatting.HasState(i.Notification))
            .Select(async item =>
            {
                var acquired = false;
                try
                {
                    await throttle.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    var details = await _client.GetSubjectAsync(account, item.Notification.SubjectApiUrl!, token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (generation != _generation || token.IsCancellationRequested || _disposed)
                        {
                            return;
                        }

                        if (details is not null && HasSubjectDetails(item.Notification.SubjectType, details))
                        {
                            _subjectCache[item.Notification.Id] = (item.Notification.UpdatedAt, details);
                        }
                    }

                    if (token.IsCancellationRequested || _disposed)
                    {
                        return;
                    }

                    if (details is not null)
                    {
                        if (!HasSubjectDetails(item.Notification.SubjectType, details))
                        {
                            GitHubRest.LogError($"GitHub API error: {item.Notification.SubjectType.ToLowerInvariant()} details missing; endpoint={GitHubRest.LogEndpoint(item.Notification.SubjectApiUrl)}.");
                        }

                        item.ApplySubject(details);
                    }
                    else
                    {
                        item.SetSubjectError(UnavailableSubjectMessage(item.Notification.SubjectType));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (GitHubApiException ex)
                {
                    lock (_lock)
                    {
                        if (generation != _generation || _disposed)
                        {
                            return;
                        }
                    }

                    if (token.IsCancellationRequested || _disposed)
                    {
                        return;
                    }

                    item.SetSubjectError(ex.Message, ex.AuthorizeUrl);
                }
                finally
                {
                    if (acquired)
                    {
                        throttle.Release();
                    }
                }
            })).ConfigureAwait(false);
    }

    private void Reset()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelLoad();
            _accountLifetime.Cancel();
            _accountLifetime.Dispose();
            _accountLifetime = new CancellationTokenSource();
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

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelLoad();
            _accountLifetime.Cancel();
            _accountLifetime.Dispose();
        }

        IsLoading = false;
        HasMoreItems = false;
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _fetching = false;
    }

    private static bool HasSubjectDetails(string subjectType, SubjectDetails details) => subjectType switch
    {
        "Issue" => details.Issue is not null,
        "PullRequest" => details.PullRequest is not null,
        _ => true,
    };

    private static string UnavailableSubjectMessage(string subjectType) => subjectType switch
    {
        "Issue" => "Couldn't load issue details. Try refreshing notifications or open it on GitHub.",
        _ => "Couldn't load pull request details. Try refreshing notifications or open it on GitHub.",
    };
}
