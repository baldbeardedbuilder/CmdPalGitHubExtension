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
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly Dictionary<string, ListLoadState> _mutations = [];
    private readonly Dictionary<string, WeakReference<IssueDetailsPage>> _detailPages = [];
    private readonly List<NotificationItem> _items = [];
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, SubjectDetails Details)> _subjectCache = [];
    private string? _mutationError;
    private Task _currentMutation = Task.CompletedTask;
    private int _accountGeneration;

    internal GitHubAccount? CurrentAccount => _auth.CurrentAccount;

    internal int AccountGeneration => _accountGeneration;

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
                return _load.CurrentLoad;
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
            if (_load.Disposed)
            {
                return [];
            }

            needsLoad = _load.NeedsLoad;
            snapshot = [.. _items];
            error = _load.Error;
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

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!_load.Disposed)
        {
            RaiseItemsChanged();
        }
    }

    public override void LoadMore() => StartLoad(reset: false);

    public Task RefreshAsync()
    {
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return _load.CurrentLoad;
            }

            _load.Invalidate();
            CancelMutations();
        }

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
        StartMutation(item, done: false);
    }

    internal void MarkAsDone(NotificationItem item)
    {
        StartMutation(item, done: true);
    }

    private void StartMutation(NotificationItem item, bool done)
    {
        GitHubAccount account;
        ListLoadState state;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_load.Disposed || _auth.CurrentAccount is not { } current
                || current != item.Account || item.AccountGeneration != _accountGeneration)
            {
                return;
            }

            account = current;
            if (_mutations.Remove(item.Notification.Id, out var previous))
            {
                previous.Dispose();
            }

            state = new ListLoadState(_lock);
            state.TryBegin(true, out operation);
            _mutations.Add(item.Notification.Id, state);
            _mutationError = null;
            if (done)
            {
                _items.Remove(item);
            }

            _currentMutation = operation.Completion.Task;
        }

        state.Publish(operation, () =>
        {
            if (!done)
            {
                item.SetUnread(false, notification => state.Publish(operation, notification));
            }
        });
        state.Publish(operation, () => RaiseItemsChanged());
        state.Run(operation, () => MutateAsync(account, item, state, operation, done), () =>
        {
            state.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to respond. Refresh and try again.", area: DiagnosticArea.Notifications,
            diagnosticEvent: done ? DiagnosticEvent.NotificationDone : DiagnosticEvent.NotificationRead, mutation: true,
            retired: () =>
            {
                lock (_lock)
                {
                    if (_mutations.TryGetValue(item.Notification.Id, out var current) && ReferenceEquals(current, state))
                    {
                        _mutations.Remove(item.Notification.Id);
                    }

                    state.Dispose();
                }
            });
    }

    private async Task MutateAsync(GitHubAccount account, NotificationItem item, ListLoadState state,
        ListLoadState.Operation operation, bool done)
    {
        try
        {
            if (done)
            {
                await _client.MarkAsDoneAsync(account, item.Notification.Id, operation.Token).ConfigureAwait(false);
            }
            else
            {
                await _client.MarkAsReadAsync(account, item.Notification.Id, operation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            lock (_lock)
            {
                if (!state.IsCurrent(operation))
                {
                    throw;
                }

                _mutationError = done ? "Couldn't mark notification as done. Refresh and try again."
                    : "Couldn't mark notification as read. Refresh and try again.";
                if (done && !_items.Any(i => i.Notification.Id == item.Notification.Id))
                {
                    _items.Add(item);
                }
            }

            if (!done)
            {
                state.Publish(operation, () => item.SetUnread(true, notification => state.Publish(operation, notification)));
            }

            throw;
        }
    }

    private Task StartLoad(bool reset)
    {
        GitHubAccount? account;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            if (account is null || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(account, operation), () =>
        {
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to respond. Try refreshing notifications.", area: DiagnosticArea.Notifications);
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        List<NotificationItem> added = [];
        var result = await _client.GetNotificationsAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        lock (_lock)
        {
            if (!_load.IsCurrent(operation))
            {
                return;
            }

            if (operation.Reset)
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
                    Action onOpened = () =>
                    {
                        if (item.Unread)
                        {
                            MarkAsRead(item);
                        }
                    };
                    foreach (var key in _detailPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
                    {
                        _detailPages.Remove(key);
                    }

                    if (!_detailPages.TryGetValue(notification.Id, out var reference)
                        || !reference.TryGetTarget(out var details) || details.IsDisposed)
                    {
                        details = issueDetails.ForNotification(notification.Id, issueApiUrl, notification.RepositoryFullName, onOpened);
                        _detailPages[notification.Id] = new(details);
                    }

                    details.SetNotification(issueApiUrl, notification.RepositoryFullName, onOpened);
                    item.Command = details;
                }

                if (_subjectCache.TryGetValue(notification.Id, out var cached) && cached.UpdatedAt == notification.UpdatedAt)
                {
                    item.ApplySubject(cached.Details);
                }

                _items.Add(item);
                added.Add(item);
            }

            _load.Succeed(operation, result.NextPage);
            _mutationError = null;
        }

        _load.Publish(operation, () => HasMoreItems = result.NextPage is not null);
        _load.Publish(operation, () => RaiseItemsChanged());
        await LoadSubjectsAsync(account, added, operation).ConfigureAwait(false);
    }

    private async Task LoadSubjectsAsync(GitHubAccount account, List<NotificationItem> items, ListLoadState.Operation load)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentSubjectRequests);
        await Task.WhenAll(items
            .Where(i => i.Subject is null && NotificationFormatting.HasState(i.Notification))
            .Select(async item =>
            {
                await throttle.WaitAsync(load.Token).ConfigureAwait(false);
                using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, DiagnosticArea.Notifications, verbose: true);
                Exception? failure = null;
                try
                {
                    load.Token.ThrowIfCancellationRequested();
                    var details = await _client.GetSubjectAsync(account, item.Notification.SubjectApiUrl!, load.Token).ConfigureAwait(false);
                    lock (_lock)
                    {
                        if (!_load.IsCurrent(load))
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

                        _load.Publish(load, () => item.ApplySubject(details, notification => _load.Publish(load, notification)));
                    }
                    else
                    {
                        failure = new System.Text.Json.JsonException();
                        _load.Publish(load, () => item.SetSubjectError(UnavailableSubjectMessage(item.Notification.SubjectType),
                            publish: notification => _load.Publish(load, notification)));
                    }
                }
                catch (OperationCanceledException) when (load.Token.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    failure = ex;
                    lock (_lock)
                    {
                        if (!_load.IsCurrent(load))
                        {
                            return;
                        }
                    }

                    _load.Publish(load, () => item.SetSubjectError(ex.Message, (ex as GitHubApiException)?.AuthorizeUrl,
                        notification => _load.Publish(load, notification)));
                }
                finally
                {
                    throttle.Release();
                    bool current;
                    lock (_lock)
                    {
                        current = _load.IsCurrent(load);
                    }

                    PageDiagnostics.Finish(operation, failure, current, cancellationToken: load.Token);
                }
            })).ConfigureAwait(false);
    }

    private void Reset()
    {
        IssueDetailsPage[] details;
        long revision;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Invalidate(reset: true);
            _accountGeneration++;
            CancelMutations();
            details = TakeDetailPages();
            _items.Clear();
            _subjectCache.Clear();
            _mutationError = null;
            revision = _load.Revision;
        }

        foreach (var page in details)
        {
            page.Dispose();
        }

        _load.Publish(revision, () => HasMoreItems = false);
        _load.Publish(revision, () => IsLoading = false);
        _load.Publish(revision, () => RaiseItemsChanged());
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void CancelMutations()
    {
        foreach (var state in _mutations.Values)
        {
            state.Dispose();
        }

        _mutations.Clear();
    }

    private IssueDetailsPage[] TakeDetailPages()
    {
        var pages = _detailPages.Values.Select(reference => reference.TryGetTarget(out var page) ? page : null)
            .OfType<IssueDetailsPage>().ToArray();
        _detailPages.Clear();
        return pages;
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        IssueDetailsPage[] details;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            CancelMutations();
            details = TakeDetailPages();
            _items.Clear();
            _subjectCache.Clear();
        }

        foreach (var page in details)
        {
            page.Dispose();
        }

        IsLoading = false;
        HasMoreItems = false;
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
