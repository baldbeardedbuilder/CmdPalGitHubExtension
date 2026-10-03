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
    private readonly IActionsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly ActionFilters _filters = new();
    private readonly Lock _lock = new();
    private readonly List<WorkflowRunItem> _items = [];
    private string? _repository;
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private CancellationTokenSource? _cancellationCts;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;
    private Task _currentCancellation = Task.CompletedTask;

    public ActionsPage(AuthService auth, IActionsClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
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
        _filters.PropChanged += (_, _) => RaiseItemsChanged();
        Filters = _filters;
        _auth.AccountChanged += OnAccountChanged;
    }

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

    internal ActionsPage ForRepository(string repository) =>
        new(_auth, _client, _browser, _time)
        {
            Id = $"{PageId}.{Uri.EscapeDataString(repository)}",
            Title = $"{repository} actions",
            _repository = repository,
        };

    internal ICommandResult OpenRepository(string repository)
    {
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
            needsLoad = !_loaded && !_fetching;
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
            empty = _auth.CurrentAccount is null
                ? Empty("Sign in to view workflow runs", "Open GitHub to sign in")
                : _error is not null
                    ? Empty("Couldn't load workflow runs", _error, refresh: true)
                    : _fetching && _items.Count == 0
                        ? Empty("Loading workflow runs...", _repository ?? string.Empty)
                        : terms.Length > 0
                            ? Empty("No workflow runs found", $"Nothing matches \"{SearchText.Trim()}\"", refresh: true)
                            : Empty("No workflow runs found", $"No {filter} workflow runs. Refresh to check for new runs", refresh: true);
            var matches = _items
                .Where(i => ActionFilters.Matches(i.Run, filter))
                .Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .Cast<IListItem>().ToList();
            if (_error is not null && _items.Count > 0)
            {
                matches.Add(new ListItem(new RefreshActionsCommand(this))
                {
                    Title = "Couldn't load workflow runs",
                    Subtitle = _error,
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
        lock (_lock)
        {
            _generation++;
            _fetching = false;
        }

        return StartLoad(reset: true);
    }

    internal Task CancelAsync(WorkflowRunItem item) => StartCancellation(item, force: false);

    internal Task ForceCancelAsync(WorkflowRunItem item) => StartCancellation(item, force: true);

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            CancelCancellation();
        }
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    private Task StartLoad(bool reset)
    {
        GitHubAccount account;
        string repository;
        int generation;
        Uri? nextPage;
        lock (_lock)
        {
            if (_auth.CurrentAccount is not { } currentAccount || _repository is not { } currentRepository
                || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _fetching = true;
            _error = null;
            account = currentAccount;
            repository = currentRepository;
            generation = _generation;
            nextPage = reset ? null : _nextPage;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, repository, nextPage, reset, generation));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, string repository, Uri? page, bool reset, int generation)
    {
        try
        {
            var result = await _client.GetRunsAsync(account, repository, page, CancellationToken.None).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            bool hasMore;
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

                var known = _items.Select(i => i.Run.Id).ToHashSet();
                _items.AddRange(result.Runs.Where(r => known.Add(r.Id)).Select(r => new WorkflowRunItem(this, repository, r, _browser, now)));
                _nextPage = result.NextPage;
                hasMore = _nextPage is not null;
                _loaded = true;
            }

            HasMoreItems = hasMore;
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

            HasMoreItems = false;
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
    }

    private Task StartCancellation(WorkflowRunItem item, bool force)
    {
        GitHubAccount account;
        CancellationTokenSource cancellation;
        int generation;
        lock (_lock)
        {
            if (_auth.CurrentAccount is not { } currentAccount || _repository is not { } repository
                || !_items.Any(i => i.Run.Id == item.Run.Id && IsActive(i.Run)))
            {
                return Task.CompletedTask;
            }

            if (_cancellationCts is not null)
            {
                if (!force)
                {
                    return _currentCancellation;
                }

                CancelCancellation();
            }

            cancellation = new CancellationTokenSource();
            _cancellationCts = cancellation;
            account = currentAccount;
            generation = _generation;
            _error = null;
            var token = cancellation.Token;
            _currentCancellation = Task.Run(() => CancelRunAsync(account, repository, item, force, generation, cancellation, token));
            return _currentCancellation;
        }
    }

    private async Task CancelRunAsync(
        GitHubAccount account,
        string repository,
        WorkflowRunItem item,
        bool force,
        int generation,
        CancellationTokenSource cancellation,
        CancellationToken token)
    {
        try
        {
            var current = await _client.GetRunAsync(account, repository, item.Run.Id, token).ConfigureAwait(false);
            if (!CanContinue(account, generation, token))
            {
                return;
            }

            UpdateRun(item, current, generation);
            if (!IsActive(current))
            {
                return;
            }

            await _client.CancelRunAsync(account, repository, current.Id, force, token).ConfigureAwait(false);
            while (CanContinue(account, generation, token))
            {
                current = await _client.GetRunAsync(account, repository, item.Run.Id, token).ConfigureAwait(false);
                if (!CanContinue(account, generation, token))
                {
                    return;
                }

                UpdateRun(item, current, generation);
                if (!IsActive(current))
                {
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
                publish = CanContinueLocked(account, generation, token);
                if (publish)
                {
                    _error = ex.Message;
                }
            }

            if (publish)
            {
                RaiseItemsChanged();
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                publish = ReferenceEquals(_cancellationCts, cancellation);
                if (publish)
                {
                    _cancellationCts = null;
                    cancellation.Dispose();
                }
            }

            if (publish)
            {
                RaiseItemsChanged();
            }
        }
    }

    private void UpdateRun(WorkflowRunItem item, GitHubWorkflowRun run, int generation)
    {
        bool publish;
        lock (_lock)
        {
            var index = _items.FindIndex(i => i.Run.Id == item.Run.Id);
            publish = generation == _generation && index >= 0;
            if (publish)
            {
                _items[index] = new WorkflowRunItem(this, _repository!, run, _browser, _time.GetUtcNow());
            }
        }

        if (publish)
        {
            RaiseItemsChanged();
        }
    }

    private bool CanContinue(GitHubAccount account, int generation, CancellationToken token)
    {
        lock (_lock)
        {
            return CanContinueLocked(account, generation, token);
        }
    }

    private bool CanContinueLocked(GitHubAccount account, int generation, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _generation && _auth.CurrentAccount == account;

    private static bool IsActive(GitHubWorkflowRun run) =>
        run.Status is "in_progress" or "queued" or "requested" or "waiting" or "pending";

    private void CancelCancellation()
    {
        _cancellationCts?.Cancel();
        _cancellationCts?.Dispose();
        _cancellationCts = null;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            CancelCancellation();
            Reset();
            _repository = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void Reset()
    {
        _generation++;
        _items.Clear();
        _nextPage = null;
        _loaded = false;
        _fetching = false;
        _error = null;
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
