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
    private readonly Lock _lock = new();
    private readonly List<WorkflowRunItem> _items = [];
    private string? _repository;
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;

    public ActionsPage(AuthService auth, IActionsClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        Id = PageId;
        Name = "Actions";
        Title = "Actions";
        Icon = Icons.Actions;
        PlaceholderText = "Filter workflow runs...";
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
            empty = _auth.CurrentAccount is null
                ? Empty("Sign in to view workflow runs", "Open GitHub to sign in")
                : _error is not null
                    ? new CommandItem(new RefreshActionsCommand(this)) { Title = "Couldn't load workflow runs", Subtitle = _error, Icon = Icons.Actions }
                    : _fetching && _items.Count == 0
                        ? Empty("Loading workflow runs...", _repository ?? string.Empty)
                        : terms.Length > 0
                            ? new CommandItem(new RefreshActionsCommand(this)) { Title = "No workflow runs found", Subtitle = $"Nothing matches \"{SearchText.Trim()}\"", Icon = Icons.Actions }
                            : new CommandItem(new RefreshActionsCommand(this)) { Title = "No workflow runs yet", Subtitle = "Refresh to check for new runs", Icon = Icons.Actions };
            var matches = _items.Where(i => terms.All(t => i.SearchText.Contains(t, StringComparison.OrdinalIgnoreCase))).Cast<IListItem>().ToList();
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

    public void Dispose() => _auth.AccountChanged -= OnAccountChanged;

    private static CommandItem Empty(string title, string subtitle) =>
        new(new NoOpCommand()) { Title = title, Subtitle = subtitle, Icon = Icons.Actions };

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
                _items.AddRange(result.Runs.Where(r => known.Add(r.Id)).Select(r => new WorkflowRunItem(this, r, _browser, now)));
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

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
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
