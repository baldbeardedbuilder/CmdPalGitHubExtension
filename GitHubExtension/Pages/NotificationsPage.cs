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
    private readonly Dictionary<string, CancellationTokenSource> _mutationCancellations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingMutations = new(StringComparer.Ordinal);
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private string? _mutationError;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;
    private Task _currentMutation = Task.CompletedTask;
    private bool _disposed;

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

    internal Task CurrentMutation
    {
        get
        {
            lock (_lock)
            {
                return _currentMutation;
            }
        }
    }

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        NotificationItem[] snapshot;
        string? error;
        string? mutationError;
        lock (_lock)
        {
            needsLoad = !_loaded && !_fetching;
            snapshot = [.. _items];
            error = _error;
            mutationError = _mutationError;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        EmptyContent = mutationError is not null
            ? _emptyContent.Get("Couldn't update notification", mutationError, refresh: true)
            : error is not null
                ? _emptyContent.Get("Couldn't load notifications", error, refresh: true)
                : _emptyContent.Get("You're all caught up", "No notifications to show");

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IListItem[] result = terms.Length == 0
            ? snapshot
            : [.. snapshot.Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))];
        if (mutationError is not null && result.Length > 0)
        {
            result = [.. result, new ListItem(new RefreshNotificationsCommand(this))
            {
                Title = "Couldn't update notification",
                Subtitle = mutationError,
                Icon = Icons.Notifications,
            }];
        }

        return result;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override void LoadMore() => StartLoad(reset: false);

    public Task RefreshAsync()
    {
        CancellationTokenSource[] cancellations;
        lock (_lock)
        {
            _generation++;
            _fetching = false;
            cancellations = InvalidateMutationsLocked();
        }

        CancelMutations(cancellations);
        return StartLoad(reset: true);
    }

    internal ICommandResult Open(NotificationItem item)
    {
        if (item.Unread)
        {
            MarkAsRead(item);
        }

        _browser.Open(item.WebUrl);
        return CommandResult.Dismiss();
    }

    internal void MarkAsRead(NotificationItem item)
    {
        if (_auth.CurrentAccount is not { } account)
        {
            return;
        }

        CancellationTokenSource cancellation;
        int generation;
        lock (_lock)
        {
            if (!CanBeginMutationLocked(account, item))
            {
                return;
            }

            generation = _generation;
            _mutationError = null;
            _pendingMutations.Add(item.Notification.Id);
            cancellation = new CancellationTokenSource();
            _mutationCancellations[item.Notification.Id] = cancellation;
        }

        item.SetUnread(false);
        lock (_lock)
        {
            _currentMutation = Task.Run(() => MutateAsync(account, item, generation, done: false, cancellation));
        }
    }

    internal void MarkAsDone(NotificationItem item)
    {
        if (_auth.CurrentAccount is not { } account)
        {
            return;
        }

        CancellationTokenSource cancellation;
        int generation;
        lock (_lock)
        {
            if (!CanBeginMutationLocked(account, item))
            {
                return;
            }

            generation = _generation;
            _mutationError = null;
            _items.Remove(item);
            _pendingMutations.Add(item.Notification.Id);
            cancellation = new CancellationTokenSource();
            _mutationCancellations[item.Notification.Id] = cancellation;
        }

        RaiseItemsChanged();
        lock (_lock)
        {
            _currentMutation = Task.Run(() => MutateAsync(account, item, generation, done: true, cancellation));
        }
    }

    private async Task MutateAsync(
        GitHubAccount account, NotificationItem item, int generation, bool done, CancellationTokenSource cancellation)
    {
        using var operation = OperationDiagnostics.Begin(
            done ? DiagnosticEvent.NotificationDone : DiagnosticEvent.NotificationRead, DiagnosticArea.Notifications);
        Exception? failure = null;
        var token = cancellation.Token;
        var mutationWasCurrent = false;
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                mutationWasCurrent = IsCurrentMutationLocked(account, item, generation, cancellation);
            }

            if (mutationWasCurrent && done)
            {
                await _client.MarkAsDoneAsync(account, item.Notification.Id, token).ConfigureAwait(false);
            }
            else if (mutationWasCurrent)
            {
                await _client.MarkAsReadAsync(account, item.Notification.Id, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        bool current;
        lock (_lock)
        {
            current = generation == _generation && !_disposed && ReferenceEquals(_auth.CurrentAccount, account);
            if (current && failure is not null)
            {
                _mutationError = done ? "Couldn't mark notification as done. Refresh and try again."
                    : "Couldn't mark notification as read. Refresh and try again.";
                if (done && !_items.Any(i => i.Notification.Id == item.Notification.Id))
                {
                    _items.Add(item);
                }
            }
        }

        if (current)
        {
            if (failure is not null && !done)
            {
                item.SetUnread(true);
            }

            RaiseItemsChanged();
        }

        lock (_lock)
        {
            current = generation == _generation;
        }

        PageDiagnostics.Finish(operation, failure, current && mutationWasCurrent, mutation: true);
        lock (_lock)
        {
            if (_mutationCancellations.TryGetValue(item.Notification.Id, out var currentCancellation)
                && ReferenceEquals(currentCancellation, cancellation))
            {
                _mutationCancellations.Remove(item.Notification.Id);
                _pendingMutations.Remove(item.Notification.Id);
            }
        }

        cancellation.Dispose();
    }

    private Task StartLoad(bool reset)
    {
        GitHubAccount? account;
        Uri? page;
        int generation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            if (_disposed || account is null || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _fetching = true;
            page = reset ? null : _nextPage;
            generation = _generation;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, page, reset, generation));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, Uri? page, bool reset, int generation)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.PageLoad, DiagnosticArea.Notifications, verbose: true);
        Exception? failure = null;
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
                    var item = new NotificationItem(this, notification, NotificationFormatting.WebUrl(account.Host, notification), _browser, now);
                    if (_issueDetails is { } issueDetails
                        && notification.SubjectType == "Issue"
                        && notification.SubjectApiUrl is { } issueApiUrl)
                    {
                        item.Command = issueDetails.ForNotification(
                            notification.Id,
                            issueApiUrl,
                            notification.RepositoryFullName,
                            () =>
                            {
                                if (item.Unread)
                                {
                                    MarkAsRead(item);
                                }
                            });
                    }

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
                _mutationError = null;
            }

            HasMoreItems = result.NextPage is not null;
        }
        catch (Exception ex)
        {
            failure = ex;
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
            bool publish;
            lock (_lock)
            {
                publish = generation == _generation;
                if (publish)
                {
                    _fetching = false;
                }
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
                if (failure is null)
                {
                    await LoadSubjectsAsync(account, added, generation).ConfigureAwait(false);
                }
            }

            lock (_lock)
            {
                publish = generation == _generation;
            }

            PageDiagnostics.Finish(operation, failure, publish);
        }
    }

    private async Task LoadSubjectsAsync(GitHubAccount account, List<NotificationItem> items, int generation)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentSubjectRequests);
        await Task.WhenAll(items
            .Where(i => i.Subject is null && NotificationFormatting.HasState(i.Notification))
            .Select(async item =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, DiagnosticArea.Notifications, verbose: true);
                Exception? failure = null;
                try
                {
                    var details = await _client.GetSubjectAsync(account, item.Notification.SubjectApiUrl!, CancellationToken.None).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (generation != _generation)
                        {
                            return;
                        }

                        if (details is not null && HasSubjectDetails(item.Notification.SubjectType, details))
                        {
                            _subjectCache[item.Notification.Id] = (item.Notification.UpdatedAt, details);
                        }
                    }

                    if (details is not null)
                    {
                        if (!HasSubjectDetails(item.Notification.SubjectType, details))
                        {
                            failure = new System.Text.Json.JsonException();
                        }

                        item.ApplySubject(details);
                    }
                    else
                    {
                        failure = new System.Text.Json.JsonException();
                        item.SetSubjectError(UnavailableSubjectMessage(item.Notification.SubjectType));
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                    lock (_lock)
                    {
                        if (generation != _generation)
                        {
                            return;
                        }
                    }

                    item.SetSubjectError(ex.Message, (ex as GitHubApiException)?.AuthorizeUrl);
                }
                finally
                {
                    throttle.Release();
                    bool current;
                    lock (_lock)
                    {
                        current = generation == _generation;
                    }

                    PageDiagnostics.Finish(operation, failure, current);
                }
            })).ConfigureAwait(false);
    }

    private void Reset()
    {
        CancellationTokenSource[] cancellations;
        lock (_lock)
        {
            _generation++;
            _items.Clear();
            _subjectCache.Clear();
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
            _mutationError = null;
            cancellations = InvalidateMutationsLocked();
        }

        CancelMutations(cancellations);
        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    public void Dispose()
    {
        CancellationTokenSource[] cancellations;
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            cancellations = InvalidateMutationsLocked();
        }

        CancelMutations(cancellations);
        IsLoading = false;
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private bool CanBeginMutationLocked(GitHubAccount account, NotificationItem item) =>
        !_disposed && ReferenceEquals(_auth.CurrentAccount, account)
        && (!_loaded || _items.Contains(item))
        && !_pendingMutations.Contains(item.Notification.Id);

    private bool IsCurrentMutationLocked(
        GitHubAccount account, NotificationItem item, int generation, CancellationTokenSource cancellation) =>
        !_disposed && generation == _generation && ReferenceEquals(_auth.CurrentAccount, account)
        && _pendingMutations.Contains(item.Notification.Id)
        && _mutationCancellations.TryGetValue(item.Notification.Id, out var current)
        && ReferenceEquals(current, cancellation);

    private CancellationTokenSource[] InvalidateMutationsLocked()
    {
        var cancellations = _mutationCancellations.Values.ToArray();
        _mutationCancellations.Clear();
        _pendingMutations.Clear();
        return cancellations;
    }

    private static void CancelMutations(IEnumerable<CancellationTokenSource> cancellations)
    {
        foreach (var cancellation in cancellations)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
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
