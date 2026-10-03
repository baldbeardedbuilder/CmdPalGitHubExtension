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
    private volatile bool _disposed;
    private string? _error;
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private Task _currentLoad = Task.CompletedTask;

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
            if (_disposed)
            {
                return CommandResult.KeepOpen();
            }

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
            CancelLoad();
        }

        return StartLoad(reset: true);
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            _disposed = true;
            CancelLoad();
        }

        IsLoading = false;
        HasMoreItems = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    private Task StartLoad(bool reset)
    {
        GitHubAccount account;
        string repository;
        int generation;
        CancellationToken token;
        Uri? nextPage;
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount is not { } currentAccount || _repository is not { } currentRepository
                || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            token = _loadCts.Token;
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
            if (generation != _generation || _disposed)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, repository, nextPage, reset, generation, token));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, string repository, Uri? page, bool reset, int generation, CancellationToken token)
    {
        try
        {
            var result = await _client.GetRunsAsync(account, repository, page, token).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            bool hasMore;
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

                var known = _items.Select(i => i.Run.Id).ToHashSet();
                _items.AddRange(result.Runs.Where(r => known.Add(r.Id)).Select(r => new WorkflowRunItem(this, repository, r, _browser, now)));
                _nextPage = result.NextPage;
                hasMore = _nextPage is not null;
                _loaded = true;
            }

            HasMoreItems = hasMore;
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

            HasMoreItems = false;
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
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelLoad();
            _items.Clear();
            _nextPage = null;
            _loaded = false;
            _error = null;
            _repository = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void Reset()
    {
        CancelLoad();
        _items.Clear();
        _nextPage = null;
        _loaded = false;
        _fetching = false;
        _error = null;
    }

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _fetching = false;
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
