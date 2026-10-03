// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// Your repos, most recently pushed first. Typing filters them instantly, then searches all of GitHub once you pause.
/// </summary>
internal sealed partial class ReposPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.repos";

    internal static readonly TimeSpan DefaultSearchDelay = TimeSpan.FromMilliseconds(300);

    private readonly AuthService _auth;
    private readonly IRepositoriesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly RepositoryIssuesPage _repositoryIssuesPage;
    private readonly RepositoryPullRequestsPage _repositoryPullRequestsPage;
    private readonly IAgentsClient? _agentsClient;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly TimeSpan _searchDelay;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<RepoItem> _mine = [];
    private readonly List<RepositoryPage> _repositoryPages = [];

    private CancellationTokenSource? _searchCts;
    private string _searchQuery = string.Empty;
    private List<RepoItem> _searchResults = [];
    private string? _searchError;
    private bool _searching;
    private Task _currentSearch = Task.CompletedTask;

    public ReposPage(
        AuthService auth,
        IRepositoriesClient client,
        IBrowserLauncher browser,
        RepositoryIssuesPage repositoryIssuesPage,
        RepositoryPullRequestsPage repositoryPullRequestsPage,
        TimeProvider? time = null,
        TimeSpan? searchDelay = null,
        ActionsPage? actions = null,
        IAgentsClient? agentsClient = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _repositoryIssuesPage = repositoryIssuesPage;
        _repositoryPullRequestsPage = repositoryPullRequestsPage;
        _agentsClient = agentsClient;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Repos, new RefreshReposCommand(this));
        _searchDelay = searchDelay ?? DefaultSearchDelay;
        Actions = actions;
        Id = PageId;
        Name = "Open";
        Title = "Repos";
        Icon = Icons.Repos;
        PlaceholderText = "Filter repos...";
        _auth.AccountChanged += OnAccountChanged;
    }

    internal ActionsPage? Actions { get; }

    /// <summary>
    /// The in flight load of your repos. Handy for tests.
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

    /// <summary>
    /// The in flight GitHub search. Handy for tests.
    /// </summary>
    internal Task CurrentSearch
    {
        get
        {
            lock (_lock)
            {
                return _currentSearch;
            }
        }
    }

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        RepoItem[] mine;
        RepoItem[] remote;
        string? error;
        string? searchError;
        bool searching;
        string searchQuery;
        lock (_lock)
        {
            needsLoad = _load.NeedsLoad;
            mine = [.. _mine];
            remote = [.. _searchResults];
            error = _load.Error;
            searchError = _searchError;
            searching = _searching;
            searchQuery = _searchQuery;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            EmptyContent = error is not null
                ? Empty("Couldn't load your repos", error, refresh: true)
                : Empty("No repos yet", "Repos you own or collaborate on show up here");
            return mine;
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var local = mine.Where(i => i.Matches(terms)).ToList();
        if (searchQuery == query)
        {
            var seen = local.Select(i => i.Repository.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            local.AddRange(remote.Where(i => seen.Add(i.Repository.FullName)));
        }

        EmptyContent = searching
            ? Empty("Searching GitHub...", string.Empty)
            : searchError is not null
                ? Empty("Couldn't search GitHub", searchError, refresh: true)
                : Empty("No repos found", $"Nothing matches \"{query}\"");

        return [.. local];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        var query = newSearch.Trim();
        if (query == oldSearch.Trim())
        {
            return;
        }

        GitHubAccount? account;
        CancellationTokenSource? cts = null;
        CancellationToken token = default;
        bool hasMore;
        bool fetching;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _searchError = null;
            account = _auth.CurrentAccount;
            hasMore = _load.NextPage is not null;
            fetching = _load.Fetching;

            if (query.Length == 0 || account is null)
            {
                _searching = false;
                _searchQuery = string.Empty;
                _searchResults = [];
            }
            else
            {
                cts = _searchCts = new CancellationTokenSource();
                _searching = true;
                token = cts.Token;
            }
        }

        // Only your own list pages; search results come back in one shot.
        HasMoreItems = cts is null && hasMore;
        IsLoading = cts is not null || fetching;
        if (cts is not null && account is not null)
        {
            lock (_lock)
            {
                if (!token.IsCancellationRequested)
                {
                    _currentSearch = Task.Run(() => SearchAsync(account, query, token));
                }
            }
        }

        RaiseItemsChanged();
    }

    public override void LoadMore()
    {
        if (SearchText.Trim().Length == 0)
        {
            StartLoad(reset: false);
        }
    }

    public Task RefreshAsync()
    {
        lock (_lock)
        {
            _load.Invalidate();
        }

        var query = SearchText;
        UpdateSearchText(string.Empty, query);
        return StartLoad(reset: true);
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        RepositoryPage[] repositoryPages;
        lock (_lock)
        {
            _load.Dispose();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            repositoryPages = [.. _repositoryPages];
            _repositoryPages.Clear();
        }

        foreach (var page in repositoryPages)
        {
            page.Dispose();
        }

        IsLoading = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    internal RepositoryPage CreateRepositoryPage(GitHubRepository repository)
    {
        var page = new RepositoryPage(_browser, Actions, repository, _repositoryIssuesPage, _repositoryPullRequestsPage, _auth, _agentsClient);
        lock (_lock)
        {
            _repositoryPages.Add(page);
        }

        return page;
    }

    private async Task SearchAsync(GitHubAccount account, string query, CancellationToken cancellationToken)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.PageSearch, DiagnosticArea.Repositories, verbose: true);
        Exception? failure = null;
        try
        {
            try
            {
                await Task.Delay(_searchDelay, cancellationToken).ConfigureAwait(false);
                var results = await _client.SearchAsync(account, query, cancellationToken).ConfigureAwait(false);
                var now = _time.GetUtcNow();
                lock (_lock)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _searchQuery = query;
                    _searchResults = [.. results.Select(r => new RepoItem(this, r, _browser, now))];
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failure = ex;
                lock (_lock)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    _searchQuery = query;
                    _searchResults = [];
                    _searchError = ex is OperationCanceledException ? "GitHub took too long to respond. Try searching again." : ex.Message;
                }
            }

            bool fetching;
            lock (_lock)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _searching = false;
                fetching = _load.Fetching;
            }

            IsLoading = fetching;
            RaiseItemsChanged();
        }
        finally
        {
            PageDiagnostics.Finish(operation, failure, true, cancellationToken: cancellationToken);
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
        return _load.Run(operation, () => LoadAsync(account, operation), () => PublishLoad(operation),
            "GitHub took too long to respond. Try refreshing repos.", area: DiagnosticArea.Repositories);
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        var result = await _client.GetMyRepositoriesAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_load.IsCurrent(operation))
            {
                return;
            }

            if (operation.Reset)
            {
                _mine.Clear();
            }

            var known = _mine.Select(i => i.Repository.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _mine.AddRange(result.Repositories
                .Where(r => known.Add(r.FullName))
                .Select(r => new RepoItem(this, r, _browser, now)));
            _load.Succeed(operation, result.NextPage);
        }
    }

    private void PublishLoad(ListLoadState.Operation operation)
    {
        bool hasMore;
        bool searching;
        lock (_lock)
        {
            hasMore = _load.NextPage is not null && SearchText.Trim().Length == 0;
            searching = _searching;
        }

        _load.Publish(operation, () => HasMoreItems = hasMore);
        _load.Publish(operation, () => IsLoading = searching);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void Reset()
    {
        RepositoryPage[] repositoryPages;
        lock (_lock)
        {
            repositoryPages = [.. _repositoryPages];
            _repositoryPages.Clear();
            _load.Invalidate(reset: true);
            _mine.Clear();
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _searching = false;
            _searchQuery = string.Empty;
            _searchResults = [];
            _searchError = null;
        }

        foreach (var repositoryPage in repositoryPages)
        {
            repositoryPage.Reset();
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }
}
