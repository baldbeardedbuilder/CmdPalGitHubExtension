// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class ActionsPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.actions";

    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IActionsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly MutationExecutor _cancelExecutor;
    private readonly HashSet<long> _normalCancellationRequested = [];
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly ActionFilters _filters = new();
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<WorkflowRunItem> _items = [];
    private readonly Dictionary<(long Id, int? Attempt), RerunWorkflowPage> _rerunPages = [];
    private string? _repository;
    private readonly ListLoadState _cancel;
    private ListLoadState.Operation? _cancellationOperation;
    private Task _currentCancellation = Task.CompletedTask;
    private string? _cancellationError;
    private Uri? _cancellationAuthorizeUrl;
    private int _accountGeneration;

    public ActionsPage(AuthService auth, IActionsClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _cancelExecutor = new MutationExecutor(auth);
        _cancel = new ListLoadState(_lock);
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Actions, new RefreshActionsCommand(this));
        Id = PageId;
        Name = "Actions";
        Title = "Actions";
        Icon = Icons.Actions;
        PlaceholderText = "Filter workflow runs...";
        _filters.CurrentFilterId = ActionFilters.Running;
        _filters.PropChanged += OnFilterChanged;
        Filters = _filters;
        _accountSubscription = auth.Subscribe(this, static page => page.OnAccountChanged(null, EventArgs.Empty));
    }

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

    internal Task CurrentCancellation
    {
        get
        {
            lock (_lock)
            {
                return _currentCancellation;
            }
        }
    }

    internal RepositoryPage? Owner { get; private init; }

    internal ActionsPage ForRepository(string repository, RepositoryPage? owner = null) =>
        new(_auth, _client, _browser, _time)
        {
            Id = $"{PageId}.{Uri.EscapeDataString(repository)}",
            Title = $"{repository} actions",
            _repository = repository,
            Owner = owner,
        };

    internal ICommandResult OpenRepository(string repository)
    {
        ClearRerunPages();
        lock (_lock)
        {
            Reset();
            _repository = repository;
        }

        SearchText = string.Empty;
        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
        return CommandResult.GoToPage(new GoToPageArgs { PageId = PageId });
    }

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return [];
            }

            needsLoad = _load.NeedsLoad;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        ICommandItem empty;
        IListItem[] result;
        lock (_lock)
        {
            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var filter = _filters.CurrentFilterId;
            var error = _cancellationError ?? _load.Error;
            empty = _auth.CurrentAccount is null
                ? Empty("Sign in to view workflow runs", "Open GitHub to sign in")
                : error is not null
                    ? Empty("Couldn't load workflow runs", error, refresh: true)
                    : _load.Fetching && _items.Count == 0
                        ? Empty("Loading workflow runs...", _repository ?? string.Empty)
                        : terms.Length > 0
                            ? Empty("No workflow runs found", $"Nothing matches \"{SearchText.Trim()}\"", refresh: true)
                            : Empty("No workflow runs found", $"No {filter} workflow runs. Refresh to check for new runs", refresh: true);
            var matches = _items
                .Where(i => ActionFilters.Matches(i.Run, filter))
                .Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Cast<IListItem>().ToList();
            if (error is not null && _items.Count > 0)
            {
                matches.Add(new ListItem(_cancellationAuthorizeUrl is { } authorize
                    ? new OpenInBrowserCommand(_browser, authorize, "Authorize organization access", Icons.Authorize)
                    : (ICommand)new RefreshActionsCommand(this))
                {
                    Title = "Couldn't load workflow runs",
                    Subtitle = error,
                    Icon = Icons.Actions,
                });
            }

            result = [.. matches];
        }

        EmptyContent = empty;
        return result;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override void LoadMore() => StartLoad(reset: false);

    public Task RefreshAsync()
    {
        if (_load.Disposed)
        {
            return _load.CurrentLoad;
        }

        CancelCancellation();
        lock (_lock)
        {
            _cancellationError = null;
            _load.Invalidate();
        }

        return StartLoad(reset: true);
    }

    internal GitHubAccount? CurrentAccount => _auth.CurrentAccount;

    internal int AccountGeneration => _accountGeneration;

    internal Task CancelAsync(WorkflowRunItem item) => StartCancellation(item, force: false);

    internal Task ForceCancelAsync(WorkflowRunItem item) => StartCancellation(item, force: true);

    internal bool CanForceCancel(long runId)
    {
        lock (_lock)
        {
            return _normalCancellationRequested.Contains(runId);
        }
    }

    internal bool IsCancellationContextCurrent(WorkflowRunItem item)
    {
        lock (_lock)
        {
            return !_load.Disposed && ReferenceEquals(item.Account, _auth.CurrentAccount)
                && _accountGeneration == item.AccountGeneration && _repository == item.Repository;
        }
    }

    internal (string Message, Uri? AuthorizeUrl) CancellationFeedback()
    {
        lock (_lock)
        {
            return (_cancellationError ?? "GitHub returned a terminal run state. Refresh the list to see the outcome.", _cancellationAuthorizeUrl);
        }
    }

    internal RerunWorkflowPage RerunPage(string repository, GitHubWorkflowRun run)
    {
        lock (_lock)
        {
            var key = (run.Id, run.RunAttempt);
            if (!_rerunPages.TryGetValue(key, out var page))
            {
                page = new RerunWorkflowPage(_auth, _client, this, repository, run);
                _rerunPages.Add(key, page);
            }

            return page;
        }
    }

    public void Dispose()
    {
        _accountSubscription.Dispose();
        _filters.PropChanged -= OnFilterChanged;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            _cancel.Dispose();
            _items.Clear();
        }

        CancelCancellation();
        ClearRerunPages();
        _cancelExecutor.Dispose();
        IsLoading = false;
        HasMoreItems = false;
    }

    private void ClearRerunPages()
    {
        RerunWorkflowPage[] pages;
        lock (_lock)
        {
            pages = [.. _rerunPages.Values];
            _rerunPages.Clear();
        }

        foreach (var page in pages)
        {
            page.Dispose();
        }
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    private Task StartLoad(bool reset)
    {
        GitHubAccount account;
        string repository;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_auth.CurrentAccount is not { } currentAccount || _repository is not { } currentRepository
                || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }

            account = currentAccount;
            repository = currentRepository;
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(account, repository, operation), () => PublishLoad(operation),
            "GitHub took too long to respond. Try refreshing workflow runs.", area: DiagnosticArea.Actions);
    }

    private async Task LoadAsync(GitHubAccount account, string repository, ListLoadState.Operation operation)
    {
        var result = await _client.GetRunsAsync(account, repository, operation.Page, operation.Token).ConfigureAwait(false);
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

            var known = _items.Select(i => i.Run.Id).ToHashSet();
            _items.AddRange(result.Runs.Where(r => known.Add(r.Id)).Select(r => new WorkflowRunItem(this, repository, r, _browser, now)));
            _load.Succeed(operation, result.NextPage);
        }
    }

    private void PublishLoad(ListLoadState.Operation operation)
    {
        bool hasMore;
        lock (_lock)
        {
            hasMore = _load.Error is null && _load.NextPage is not null;
        }

        _load.Publish(operation, () => HasMoreItems = hasMore);
        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private Task StartCancellation(WorkflowRunItem item, bool force)
    {
        GitHubAccount account;
        string repository;
        ListLoadState.Operation cancellation;
        lock (_lock)
        {
            if (_load.Disposed || item.Account is null || _auth.CurrentAccount is not { } currentAccount
                || currentAccount != item.Account || _accountGeneration != item.AccountGeneration
                || _repository is not { } currentRepository || !string.Equals(currentRepository, item.Repository, StringComparison.Ordinal)
                || !_items.Any(i => i.Run.Id == item.Run.Id && IsActive(i.Run)))
            {
                return Task.CompletedTask;
            }

            if (force && !_normalCancellationRequested.Contains(item.Run.Id))
            {
                return Task.CompletedTask;
            }

            if (_cancellationOperation is not null)
            {
                if (!force)
                {
                    return _currentCancellation;
                }

                CancelCancellationLocked();
            }

            _cancel.TryBegin(true, out cancellation);
            _cancellationOperation = cancellation;
            _cancellationError = null;
            _cancellationAuthorizeUrl = null;
            account = currentAccount;
            repository = currentRepository;
            var token = cancellation.Token;
            _currentCancellation = _cancel.Run(cancellation, () => CancelRunAsync(account, repository, item, force, cancellation, token),
                () =>
                {
                    lock (_lock)
                    {
                        if (_cancel.IsCurrent(cancellation) && _cancel.Error is { } error)
                        {
                            _cancellationError = error;
                        }
                    }

                    _cancel.Publish(cancellation, () => RaiseItemsChanged());
                },
                "GitHub took too long to respond. Refresh workflow runs to check their state.", area: DiagnosticArea.Actions,
                mutation: true, retired: () =>
                {
                    lock (_lock)
                    {
                        if (ReferenceEquals(_cancellationOperation, cancellation))
                        {
                            _cancellationOperation = null;
                        }
                    }
                });
            return _currentCancellation;
        }
    }

    private async Task CancelRunAsync(
        GitHubAccount account,
        string repository,
        WorkflowRunItem item,
        bool force,
        ListLoadState.Operation cancellation,
        CancellationToken token)
    {
        try
        {
            GitHubWorkflowRun? verified = null;
            async Task<MutationResult<GitHubWorkflowRun>> Reconcile(CancellationToken operationToken)
            {
                verified = await _client.GetRunAsync(account, repository, item.Run.Id, operationToken).ConfigureAwait(false);
                return verified.Status == "completed" ? new(MutationState.Completed, verified)
                    : IsActive(verified) ? new(MutationState.Pending, verified, "Cancellation was requested or may have been accepted. GitHub hasn't stopped the run yet.")
                    : new(MutationState.Unknown, verified, "GitHub returned an unknown workflow state. Refresh before retrying.");
            }

            var result = await _cancelExecutor.ExecuteAsync(account,
                $"workflow:{repository.ToLowerInvariant()}:{item.Run.Id}:{(force ? "force" : "cancel")}",
                async operationToken =>
                {
                    verified = await _client.GetRunAsync(account, repository, item.Run.Id, operationToken).ConfigureAwait(false);
                    if (!CanContinue(account, repository, cancellation, operationToken) || !IsActive(verified))
                    {
                        return false;
                    }

                    if (_client is IWorkflowCancellationPermissionsClient permissions
                        && !await permissions.CanCancelAsync(account, repository, operationToken).ConfigureAwait(false))
                    {
                        throw new GitHubApiException("You need repository write access and Actions write permission to cancel this workflow.");
                    }

                    return CanContinue(account, repository, cancellation, operationToken);
                },
                async operationToken =>
                {
                    await _client.CancelRunAsync(account, repository, item.Run.Id, force, operationToken).ConfigureAwait(false);
                    operationToken.ThrowIfCancellationRequested();
                    if (!CanContinue(account, repository, cancellation, operationToken))
                    {
                        return new MutationResult<GitHubWorkflowRun>(MutationState.Stale);
                    }

                    if (!force)
                    {
                        lock (_lock)
                        {
                            if (CanContinueLocked(account, repository, cancellation, operationToken))
                            {
                                _normalCancellationRequested.Add(item.Run.Id);
                            }
                        }
                    }

                    try
                    {
                        return await Reconcile(operationToken).ConfigureAwait(false);
                    }
                    catch (GitHubApiException ex)
                    {
                        var error = new GitHubApiException(ex.Message, ex, ex.AuthorizeUrl, outcomeUnknown: true);
                        OperationDiagnostics.CorrelateFailure(ex, error);
                        throw error;
                    }
                }, Reconcile, token).ConfigureAwait(false);
            if (!CanContinue(account, repository, cancellation, token))
            {
                return;
            }

            if (verified is not null)
            {
                UpdateRun(item, verified, account, repository, cancellation, token);
            }

            if (verified?.Status == "completed")
            {
                return;
            }

            if (result.State is not (MutationState.Pending or MutationState.Completed))
            {
                lock (_lock)
                {
                    if (CanContinueLocked(account, repository, cancellation, token))
                    {
                        _cancellationError = verified is not null && !IsActive(verified)
                            ? "GitHub returned an unknown workflow state. Refresh before retrying."
                            : result.Error ?? "Couldn't request cancellation. Refresh to check GitHub before retrying.";
                        _cancellationAuthorizeUrl = result.AuthorizeUrl;
                    }
                }

                return;
            }

            while (CanContinue(account, repository, cancellation, token))
            {
                var current = await _client.GetRunAsync(account, repository, item.Run.Id, token).WaitAsync(token).ConfigureAwait(false);
                if (!CanContinue(account, repository, cancellation, token))
                {
                    return;
                }

                UpdateRun(item, current, account, repository, cancellation, token);
                if (current.Status == "completed")
                {
                    _cancelExecutor.ObserveCompletion(account,
                        $"workflow:{repository.ToLowerInvariant()}:{item.Run.Id}:{(force ? "force" : "cancel")}");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (GitHubApiException ex)
        {
            bool publish;
            lock (_lock)
            {
                publish = CanContinueLocked(account, repository, cancellation, token);
                if (publish)
                {
                    _cancellationError = ex.Message;
                    _cancellationAuthorizeUrl = ex.AuthorizeUrl;
                }
            }

            if (publish)
            {
                RaiseItemsChanged();
            }
        }
    }

    private void UpdateRun(
        WorkflowRunItem item,
        GitHubWorkflowRun run,
        GitHubAccount account,
        string repository,
        ListLoadState.Operation cancellation,
        CancellationToken token)
    {
        bool publish;
        lock (_lock)
        {
            var index = _items.FindIndex(i => i.Run.Id == item.Run.Id);
            publish = CanContinueLocked(account, repository, cancellation, token) && index >= 0;
            if (publish)
            {
                _items[index] = new WorkflowRunItem(this, repository, run, _browser, _time.GetUtcNow());
            }
        }

        if (publish)
        {
            RaiseItemsChanged();
        }
    }

    private bool CanContinue(GitHubAccount account, string repository, ListLoadState.Operation cancellation, CancellationToken token)
    {
        lock (_lock)
        {
            return CanContinueLocked(account, repository, cancellation, token);
        }
    }

    private bool CanContinueLocked(GitHubAccount account, string repository, ListLoadState.Operation cancellation, CancellationToken token) =>
        !token.IsCancellationRequested
        && ReferenceEquals(_cancellationOperation, cancellation)
        && string.Equals(_repository, repository, StringComparison.Ordinal)
        && !_load.Disposed
        && _auth.CurrentAccount == account;

    private static bool IsActive(GitHubWorkflowRun run) =>
        run.Status is "in_progress" or "queued" or "requested" or "waiting" or "pending";

    private void CancelCancellation()
    {
        lock (_lock)
        {
            _accountGeneration++;
            CancelCancellationLocked();
        }
    }

    private void CancelCancellationLocked()
    {
        _cancel.Invalidate();
        _cancellationOperation = null;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        ClearRerunPages();
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            CancelCancellationLocked();
            _cancellationError = null;
            _normalCancellationRequested.Clear();
            Reset();
            _repository = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void Reset()
    {
        _accountGeneration++;
        CancelCancellationLocked();
        _cancellationError = null;
        _normalCancellationRequested.Clear();
        _load.Invalidate(reset: true);
        _items.Clear();
    }

    private void OnFilterChanged(object? sender, IPropChangedEventArgs e)
    {
        if (!_load.Disposed)
        {
            RaiseItemsChanged();
        }
    }
}

internal sealed partial class ActionFilters : Filters
{
    internal const string Running = "running";
    internal const string Succeeded = "succeeded";
    internal const string Failed = "failed";

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = Running, Name = "Running", Icon = Icons.RunInProgress },
        new Filter { Id = Succeeded, Name = "Succeeded", Icon = Icons.RunSuccess },
        new Filter { Id = Failed, Name = "Failed", Icon = Icons.RunFailure },
    ];

    internal static bool Matches(GitHubWorkflowRun run, string filter) => filter switch
    {
        Running => run.Status is "in_progress" or "queued" or "requested" or "waiting" or "pending",
        Succeeded => run.Status == "completed" && run.Conclusion == "success",
        Failed => run.Status == "completed" && run.Conclusion != "success",
        _ => false,
    };
}
