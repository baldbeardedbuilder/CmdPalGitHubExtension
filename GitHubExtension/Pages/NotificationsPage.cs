// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

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
    private readonly MutationExecutor _executor;
    private readonly IssueDetailsPage? _issueDetails;
    private readonly IPullRequestActionsClient? _pullRequestActionsClient;
    private readonly TimeProvider _time;
    private readonly WorkItemDetailsCache _nativeDetails;
    private readonly PageEmptyContent _emptyContent;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly Dictionary<string, ListLoadState> _mutations = [];
    private readonly Dictionary<string, WeakReference<IssueDetailsPage>> _detailPages = [];
    private readonly Dictionary<string, PullRequestActionsPage> _pullRequestActionPages = [];
    private readonly List<NotificationItem> _items = [];
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, SubjectDetails Details)> _subjectCache = [];
    private string? _mutationError;
    private Uri? _authorizeUrl;
    private Uri? _authorizeCommandUrl;
    private ICommand? _authorizeCommand;
    private Task _currentMutation = Task.CompletedTask;
    private int _accountGeneration;

    internal GitHubAccount? CurrentAccount => _auth.CurrentAccount;

    internal int AccountGeneration => _accountGeneration;

    public NotificationsPage(AuthService auth, INotificationsClient client, IBrowserLauncher browser, TimeProvider? time = null,
        IssueDetailsPage? issueDetails = null,
        IThreadSubscriptionsClient? subscriptionsClient = null,
        IPullRequestActionsClient? pullRequestActionsClient = null,
        WorkItemDetailFactories? workItemDetailFactories = null)
    {
        _auth = auth;
        _client = client;
        _subscriptionsClient = subscriptionsClient ?? client as IThreadSubscriptionsClient;
        _pullRequestActionsClient = pullRequestActionsClient;
        _nativeDetails = new(workItemDetailFactories);
        _browser = browser;
        _executor = new MutationExecutor(auth);
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
        ICommand? authorizeCommand;
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
            if (_authorizeUrl != _authorizeCommandUrl)
            {
                _authorizeCommandUrl = _authorizeUrl;
                _authorizeCommand = _authorizeUrl is { } authorize
                    ? new OpenInBrowserCommand(_browser, authorize, "Authorize organization access", Icons.Notifications)
                    : null;
            }

            authorizeCommand = _authorizeCommand;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        var empty = mutationError is not null
            ? _emptyContent.Get("Couldn't update notification", mutationError, refresh: true, command: authorizeCommand)
            : error is not null
                ? _emptyContent.Get("Couldn't load notifications", error, refresh: true, command: authorizeCommand)
                : _emptyContent.Get("You're all caught up", "No notifications to show");
        empty.MoreCommands = BrowsingCommands();
        EmptyContent = empty;

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IListItem[] result = terms.Length == 0
            ? snapshot
            : [.. snapshot.Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))];
        if ((mutationError ?? error) is { } message && result.Length > 0)
        {
            result = [.. result, new ListItem(authorizeCommand ?? new RefreshNotificationsCommand(this))
            {
                Title = mutationError is null ? "Couldn't load notifications" : "Couldn't update notification",
                Subtitle = message,
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
        PullRequestActionsPage[] pullRequestActions = [];
        bool previewsOnly;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return _load.CurrentLoad;
            }

            previewsOnly = _client is INotificationBrowsingClient && _time.GetUtcNow() < _nextPoll;
            if (!previewsOnly)
            {
                _load.Invalidate();
                pullRequestActions = TakePullRequestActionPages();
            }
        }

        foreach (var page in pullRequestActions)
        {
            page.Dispose();
        }

        return previewsOnly ? RetrySubjectsAsync() : StartLoad(reset: true);
    }

    private Task RetrySubjectsAsync()
    {
        ListLoadState.Operation operation;
        GitHubAccount account;
        List<NotificationItem> items;
        lock (_lock)
        {
            items = _items.Where(item => item.Subject is null && NotificationFormatting.HasState(item.Notification)).ToList();
            if (_auth.CurrentAccount is not { } current || items.Count == 0 || !_load.TryBegin(true, out operation))
            {
                return _load.CurrentLoad;
            }
            account = current;
            _load.Succeed(operation, _load.NextPage);
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadSubjectsAsync(account, items, operation), () =>
        {
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to return notification details.", area: DiagnosticArea.Notifications);
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
                || current != item.Account || item.AccountGeneration != _accountGeneration || !_items.Contains(item))
            {
                return;
            }

            account = current;
            if (_mutations.ContainsKey(item.Notification.Id))
            {
                return;
            }

            state = new ListLoadState(_lock);
            state.TryBegin(true, out operation);
            _mutations.Add(item.Notification.Id, state);
            _mutationError = null;
            _authorizeUrl = null;

            _currentMutation = operation.Completion.Task;
        }

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
            }, diagnose: false);
    }

    private async Task MutateAsync(GitHubAccount account, NotificationItem item, ListLoadState state,
        ListLoadState.Operation operation, bool done)
    {
        using var diagnostic = OperationDiagnostics.Begin(
            done ? DiagnosticEvent.NotificationDone : DiagnosticEvent.NotificationRead, DiagnosticArea.Notifications);
        Exception? failure = null;
        async Task<MutationResult<bool>> Reconcile(CancellationToken token)
        {
            Uri? next = null;
            do
            {
                var page = await _client.GetNotificationsAsync(account, next, token).ConfigureAwait(false);
                var notification = page.Notifications.FirstOrDefault(n => n.Id == item.Notification.Id);
                if (notification is not null)
                {
                    return !done && !notification.Unread
                        ? new MutationResult<bool>(MutationState.Completed, true)
                        : new MutationResult<bool>(MutationState.Pending, Error: "GitHub may still be updating this notification. Refresh before retrying.");
                }

                next = page.NextPage;
            }
            while (next is not null);
            return new MutationResult<bool>(MutationState.Completed, true);
        }

        var result = await _executor.ExecuteAsync(
            account, $"notification:{item.Notification.Id}:{(done ? "done" : "read")}",
            async token =>
            {
                lock (_lock)
                {
                    if (!state.IsCurrent(operation)
                        || item.AccountGeneration != _accountGeneration
                        || !_items.Any(current => current.Notification.Id == item.Notification.Id
                            && current.Notification.RepositoryFullName == item.Notification.RepositoryFullName))
                    {
                        return false;
                    }
                }

                Uri? next = null;
                do
                {
                    var fresh = await _client.GetNotificationsAsync(account, next, token).ConfigureAwait(false);
                    var target = fresh.Notifications.FirstOrDefault(n => n.Id == item.Notification.Id);
                    if (target is not null)
                    {
                        lock (_lock)
                        {
                            return state.IsCurrent(operation) && item.AccountGeneration == _accountGeneration
                                && target.RepositoryFullName == item.Notification.RepositoryFullName;
                        }
                    }

                    next = fresh.NextPage;
                }
                while (next is not null);
                return false;
            },
            async token =>
            {
                lock (_lock)
                {
                    if (!state.IsCurrent(operation) || item.AccountGeneration != _accountGeneration)
                    {
                        return new MutationResult<bool>(MutationState.Stale);
                    }
                }
                try
                {
                    if (done)
                    {
                        await _client.MarkAsDoneAsync(account, item.Notification.Id, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await _client.MarkAsReadAsync(account, item.Notification.Id, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                    throw;
                }

                try
                {
                    return await Reconcile(token).ConfigureAwait(false);
                }
                catch (GitHubApiException ex)
                {
                    failure = ex;
                    return new MutationResult<bool>(MutationState.Unknown, Error: ex.Message, AuthorizeUrl: ex.AuthorizeUrl);
                }
            },
            Reconcile, cancellationToken: operation.Token).ConfigureAwait(false);
        var outcome = result.State switch
        {
            MutationState.Completed => DiagnosticOutcome.Completed,
            MutationState.Pending => DiagnosticOutcome.Accepted,
            MutationState.Unknown => DiagnosticOutcome.Unknown,
            MutationState.Stale => DiagnosticOutcome.Cancelled,
            _ => DiagnosticOutcome.Failed,
        };
        if (failure is not null && result.State != MutationState.Stale)
        {
            diagnostic.Fail(failure, outcome: outcome);
        }
        else
        {
            diagnostic.Complete(diagnostic.ChildOutcome == outcome ? null : outcome);
        }

        lock (_lock)
        {
            if (!state.IsCurrent(operation) || item.AccountGeneration != _accountGeneration
                || result.State == MutationState.Stale || !_executor.IsCurrent(account))
            {
                return;
            }

            if (result.State != MutationState.Completed)
            {
                _mutationError = result.Error ?? "GitHub is still processing this notification. Refresh to check its state.";
                _authorizeUrl = result.AuthorizeUrl;
            }
        }

        if (result.State == MutationState.Completed)
        {
            await RefreshAfterMutationAsync().ConfigureAwait(false);
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
            lock (_lock)
            {
                if (_load.IsCurrent(operation) && _load.AuthorizeUrl is { } authorize)
                {
                    _authorizeUrl = authorize;
                }
            }

            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to respond. Try refreshing notifications.", area: DiagnosticArea.Notifications);
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        List<NotificationItem> added = [];
        NotificationsPageResult result;
        if (_client is INotificationBrowsingClient browsing)
        {
            NotificationQuery query;
            DateTimeOffset? modified;
            lock (_lock) { query = _query; modified = _forceRefresh ? null : _lastModified; _forceRefresh = false; }
            NotificationPollResult poll;
            try
            {
                poll = await browsing.GetNotificationsAsync(account, query, operation.Page, modified, operation.Token).ConfigureAwait(false);
            }
            catch (GitHubApiException ex) when (ex.Data["NotificationPollInterval"] is TimeSpan interval)
            {
                lock (_lock)
                {
                    if (_load.IsCurrent(operation) && operation.Page is null) { _nextPoll = _time.GetUtcNow() + interval; }
                }

                throw;
            }
            lock (_lock)
            {
                if (!_load.IsCurrent(operation) || _auth.CurrentAccount != account) { return; }
                if (operation.Page is null)
                {
                    _lastModified = poll.LastModified;
                    _nextPoll = _time.GetUtcNow() + poll.PollInterval;
                }

                if (poll.NotModified)
                {
                    _load.Succeed(operation, _load.NextPage);
                    added = _items.Where(item => item.Subject is null).ToList();
                }
            }

            if (poll.NotModified)
            {
                await LoadSubjectsAsync(account, added, operation).ConfigureAwait(false);
                return;
            }

            result = new(poll.Notifications, poll.NextPage);
        }
        else
        {
            result = await _client.GetNotificationsAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
        }
        var now = _time.GetUtcNow();

        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || _auth.CurrentAccount != account)
            {
                return;
            }

            if (operation.Reset)
            {
                _items.Clear();
            }

            foreach (var notification in result.Notifications.Where(n => !n.Unread))
            {
                _executor.ObserveCompletion(account, $"notification:{notification.Id}:read");
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
                    Action<BaldBeardedBuilder.CmdPal.GitHub.Issues.GitHubIssue> onChanged =
                        issue => { _ = RefreshAfterIssueMutationAsync(item); };
                    foreach (var key in _detailPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
                    {
                        _detailPages.Remove(key);
                    }

                    if (!_detailPages.TryGetValue(notification.Id, out var reference)
                        || !reference.TryGetTarget(out var details) || details.IsDisposed)
                    {
                        details = issueDetails.ForNotification(notification.Id, issueApiUrl, notification.RepositoryFullName, onOpened, onChanged);
                        _detailPages[notification.Id] = new(details);
                    }

                    details.SetNotification(issueApiUrl, notification.RepositoryFullName, onOpened, onChanged);
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
            _authorizeUrl = null;
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
                        if (!_load.IsCurrent(load) || _auth.CurrentAccount != account)
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
                        if (!_load.IsCurrent(load) || _auth.CurrentAccount != account)
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
        ThreadSubscriptionPage[] subscriptions;
        PullRequestActionsPage[] pullRequestActions;
        long revision;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Invalidate(reset: true);
            Interlocked.Increment(ref _accountGeneration);
            CancelMutations();
            details = TakeDetailPages();
            subscriptions = TakeSubscriptionPages();
            pullRequestActions = TakePullRequestActionPages();
            _items.Clear();
            _subjectCache.Clear();
            _query = new();
            _lastModified = null;
            _nextPoll = default;
            _forceRefresh = false;
            _browsingCommands = null;
            _pollLifetime.Cancel();
            _pollLifetime.Dispose();
            _pollLifetime = new();
            _mutationError = null;
            revision = _load.Revision;
            _authorizeUrl = null;
        }

        _nativeDetails.Dispose();
        foreach (var page in details)
        {
            page.Dispose();
        }

        foreach (var page in subscriptions)
        {
            page.Dispose();
        }
        foreach (var page in pullRequestActions)
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
        ThreadSubscriptionPage[] subscriptions;
        PullRequestActionsPage[] pullRequestActions;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            Interlocked.Increment(ref _accountGeneration);
            _load.Dispose();
            CancelMutations();
            details = TakeDetailPages();
            subscriptions = TakeSubscriptionPages();
            pullRequestActions = TakePullRequestActionPages();
            _items.Clear();
            _subjectCache.Clear();
            _pollLifetime.Cancel();
            _pollLifetime.Dispose();
        }

        _nativeDetails.Dispose();
        foreach (var page in details)
        {
            page.Dispose();
        }

        foreach (var page in subscriptions)
        {
            page.Dispose();
        }
        foreach (var page in pullRequestActions)
        {
            page.Dispose();
        }

        _executor.Dispose();
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
