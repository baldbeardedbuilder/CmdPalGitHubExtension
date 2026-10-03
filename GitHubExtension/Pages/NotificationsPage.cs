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
    private readonly MutationExecutor _mutations;
    private readonly IssueDetailsPage? _issueDetails;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly Lock _lock = new();
    private readonly List<NotificationItem> _items = [];
    private readonly Dictionary<string, (DateTimeOffset UpdatedAt, SubjectDetails Details)> _subjectCache = [];
    private Uri? _nextPage;
    private GitHubAccount? _loadedAccount;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private Uri? _authorizeUrl;
    private Uri? _authorizeCommandUrl;
    private ICommand? _authorizeCommand;
    private bool _disposed;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;
    internal Task CurrentMutation { get; private set; } = Task.CompletedTask;

    public NotificationsPage(AuthService auth, INotificationsClient client, IBrowserLauncher browser, TimeProvider? time = null, IssueDetailsPage? issueDetails = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _mutations = new MutationExecutor(auth);
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
        ICommand? authorizeCommand;
        lock (_lock)
        {
            needsLoad = !_loaded && !_fetching;
            snapshot = [.. _items];
            error = _error;
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

        EmptyContent = error is not null
            ? _emptyContent.Get("Couldn't load notifications", error, refresh: true, command: authorizeCommand)
            : _emptyContent.Get("You're all caught up", "No notifications to show");

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IListItem[] result = terms.Length == 0
            ? snapshot
            : [.. snapshot.Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))];
        if (error is not null && result.Length > 0)
        {
            ICommand command = authorizeCommand ?? new RefreshNotificationsCommand(this);
            result = [.. result, new ListItem(command) { Title = "Couldn't update notifications", Subtitle = error }];
        }
        return result;
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
        => StartMutation(item, done: false);

    internal void MarkAsDone(NotificationItem item)
        => StartMutation(item, done: true);

    private void StartMutation(NotificationItem item, bool done)
    {
        GitHubAccount account;
        lock (_lock)
        {
            if (_disposed || !_items.Contains(item) || _auth.CurrentAccount is not { } current
                || !ReferenceEquals(current, _loadedAccount))
            {
                return;
            }

            account = current;
            CurrentMutation = Task.Run(() => MutateAsync(account, item, done));
        }
    }

    private async Task MutateAsync(GitHubAccount account, NotificationItem item, bool done)
    {
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

        var result = await _mutations.ExecuteAsync(
            account, $"notification:{item.Notification.Id}:{(done ? "done" : "read")}",
            async token =>
            {
                lock (_lock)
                {
                    if (_disposed || !_items.Contains(item))
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
                        return target.RepositoryFullName == item.Notification.RepositoryFullName;
                    }

                    next = fresh.NextPage;
                }
                while (next is not null);
                return false;
            },
            async token =>
            {
                if (done)
                {
                    await _client.MarkAsDoneAsync(account, item.Notification.Id, token).ConfigureAwait(false);
                }
                else
                {
                    await _client.MarkAsReadAsync(account, item.Notification.Id, token).ConfigureAwait(false);
                }

                try
                {
                    return await Reconcile(token).ConfigureAwait(false);
                }
                catch (GitHubApiException ex)
                {
                    return new MutationResult<bool>(MutationState.Unknown, Error: ex.Message, AuthorizeUrl: ex.AuthorizeUrl);
                }
            },
            Reconcile).ConfigureAwait(false);
        if (result.State == MutationState.Stale || !_mutations.IsCurrent(account))
        {
            return;
        }

        if (result.State == MutationState.Completed)
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        else
        {
            lock (_lock)
            {
                if (_disposed || !_mutations.IsCurrent(account))
                {
                    return;
                }

                _error = result.Error ?? "GitHub is still processing this notification. Refresh to check its state.";
                _authorizeUrl = result.AuthorizeUrl;
            }

            RaiseItemsChanged();
        }
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
            if (generation != _generation || !ReferenceEquals(account, _auth.CurrentAccount))
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, page, reset, generation));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, Uri? page, bool reset, int generation)
    {
        List<NotificationItem> added = [];
        try
        {
            var result = await _client.GetNotificationsAsync(account, page, CancellationToken.None).ConfigureAwait(false);
            foreach (var notification in result.Notifications.Where(n => !n.Unread))
            {
                _mutations.ObserveCompletion(account, $"notification:{notification.Id}:read");
            }
            var now = _time.GetUtcNow();

            lock (_lock)
            {
                if (generation != _generation || !ReferenceEquals(account, _auth.CurrentAccount))
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
                _loadedAccount = account;
                _error = null;
                _authorizeUrl = null;
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
                _authorizeUrl = ex.AuthorizeUrl;
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
            }
        }

        await LoadSubjectsAsync(account, added, generation).ConfigureAwait(false);
    }

    private async Task LoadSubjectsAsync(GitHubAccount account, List<NotificationItem> items, int generation)
    {
        using var throttle = new SemaphoreSlim(MaxConcurrentSubjectRequests);
        await Task.WhenAll(items
            .Where(i => i.Subject is null && NotificationFormatting.HasState(i.Notification))
            .Select(async item =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
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
                            GitHubRest.LogError($"GitHub API error: {item.Notification.SubjectType.ToLowerInvariant()} details missing; endpoint={GitHubRest.LogEndpoint(item.Notification.SubjectApiUrl)}.");
                        }

                        item.ApplySubject(details);
                    }
                    else
                    {
                        item.SetSubjectError(UnavailableSubjectMessage(item.Notification.SubjectType));
                    }
                }
                catch (GitHubApiException ex)
                {
                    lock (_lock)
                    {
                        if (generation != _generation)
                        {
                            return;
                        }
                    }

                    item.SetSubjectError(ex.Message, ex.AuthorizeUrl);
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
            _loadedAccount = null;
            _fetching = false;
            _error = null;
            _authorizeUrl = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        _mutations.Dispose();
        lock (_lock)
        {
            _disposed = true;
            _generation++;
        }
        IsLoading = false;
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
