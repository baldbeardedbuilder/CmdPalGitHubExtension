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
    private readonly Lock _lock = new();
    private readonly List<RepoItem> _mine = [];
    private readonly List<RepositoryPage> _repositoryPages = [];
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;

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
                return _currentLoad;
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
            needsLoad = !_loaded && !_fetching;
            mine = [.. _mine];
            remote = [.. _searchResults];
            error = _error;
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
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            _searchError = null;
            account = _auth.CurrentAccount;
            hasMore = _nextPage is not null;
            fetching = _fetching;

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
            _generation++;
            _fetching = false;
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
                fetching = _fetching;
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
        Uri? page;
        int generation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            if (account is null || _fetching || (!reset && _nextPage is null))
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
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.PageLoad, DiagnosticArea.Repositories, verbose: true);
        Exception? failure = null;
        try
        {
            var result = await _client.GetMyRepositoriesAsync(account, page, CancellationToken.None).ConfigureAwait(false);
            var now = _time.GetUtcNow();

            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                if (reset)
                {
                    _mine.Clear();
                }

                var known = _mine.Select(i => i.Repository.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _mine.AddRange(result.Repositories
                    .Where(r => known.Add(r.FullName))
                    .Select(r => new RepoItem(this, r, _browser, now)));

                _nextPage = result.NextPage;
                _loaded = true;
                _error = null;
            }

            HasMoreItems = result.NextPage is not null && SearchText.Trim().Length == 0;
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
            bool searching;
            bool publish;
            lock (_lock)
            {
                publish = generation == _generation;
                if (publish)
                {
                    _fetching = false;
                }

                searching = _searching;
            }

            if (publish)
            {
                IsLoading = searching;
                RaiseItemsChanged();
            }

            lock (_lock)
            {
                publish = generation == _generation;
            }

            PageDiagnostics.Finish(operation, failure, publish);
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void Reset()
    {
        RepositoryPage[] repositoryPages;
        lock (_lock)
        {
            repositoryPages = [.. _repositoryPages];
            _repositoryPages.Clear();
            _generation++;
            _mine.Clear();
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
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
