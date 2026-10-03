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
    private readonly Dictionary<string, WeakReference<RepositoryPage>> _repositoryPages = new(StringComparer.OrdinalIgnoreCase);

    private readonly ListLoadState _search;
    private string _searchQuery = string.Empty;
    private List<RepoItem> _searchResults = [];
    private string? _searchError;
    private bool _searching;
    private int _accountGeneration;

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
        _search = new ListLoadState(_lock);
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

    internal GitHubAccount? CurrentAccount => _auth.CurrentAccount;

    internal int AccountGeneration => _accountGeneration;

    internal bool CanNavigate(GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            return !_load.Disposed && account is not null && _auth.CurrentAccount == account && _accountGeneration == generation;
        }
    }

    internal int LiveRepositoryPages
    {
        get
        {
            lock (_lock)
            {
                return _repositoryPages.Values.Count(reference => reference.TryGetTarget(out _));
            }
        }
    }

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
                return _search.CurrentLoad;
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
            if (_load.Disposed)
            {
                return [];
            }

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
        ListLoadState.Operation? operation = null;
        bool hasMore;
        bool fetching;
        long revision;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _search.Invalidate(reset: true);
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
                _search.TryBegin(true, out operation);
                _searching = true;
            }

            revision = _search.Revision;
        }

        // Only your own list pages; search results come back in one shot.
        _search.Publish(revision, () => HasMoreItems = operation is null && hasMore);
        _search.Publish(revision, () => IsLoading = operation is not null || fetching);
        if (operation is not null && account is not null)
        {
            _search.Run(operation, () => SearchAsync(account, query, operation), () => PublishSearch(query, operation),
                "GitHub took too long to respond. Try searching again.",
                area: DiagnosticArea.Repositories, diagnosticEvent: DiagnosticEvent.PageSearch);
        }

        _search.Publish(revision, () => RaiseItemsChanged());
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
            if (_load.Disposed)
            {
                return _load.CurrentLoad;
            }

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
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            _search.Dispose();
            repositoryPages = TakeRepositoryPages();
            _mine.Clear();
            _searchResults.Clear();
        }

        foreach (var page in repositoryPages)
        {
            page.Dispose();
        }

        IsLoading = false;
        HasMoreItems = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    internal RepositoryPage CreateRepositoryPage(GitHubRepository repository)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_load.Disposed, this);
            foreach (var key in _repositoryPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
            {
                _repositoryPages.Remove(key);
            }

            if (_repositoryPages.TryGetValue(repository.FullName, out var reference)
                && reference.TryGetTarget(out var existing) && !existing.IsDisposed)
            {
                existing.UpdateRepository(repository);
                return existing;
            }

            var page = new RepositoryPage(_browser, Actions, repository, _repositoryIssuesPage, _repositoryPullRequestsPage, _auth, _agentsClient);
            _repositoryPages[repository.FullName] = new(page);
            return page;
        }
    }

    internal RepositoryPage? CreateRepositoryPage(GitHubRepository repository, GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            return CanNavigate(account, generation) ? CreateRepositoryPage(repository) : null;
        }
    }

    private async Task SearchAsync(GitHubAccount account, string query, ListLoadState.Operation search)
    {
        var cancellationToken = search.Token;
        await Task.Delay(_searchDelay, cancellationToken).ConfigureAwait(false);
        var results = await _client.SearchAsync(account, query, cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_search.IsCurrent(search))
            {
                return;
            }

            _searchQuery = query;
            _searchResults = [.. results.Select(r => new RepoItem(this, r, _browser, now, account))];
            _search.Succeed(search, null);
        }
    }

    private void PublishSearch(string query, ListLoadState.Operation search)
    {
        bool fetching;
        lock (_lock)
        {
            if (!_search.IsCurrent(search))
            {
                return;
            }

            _searching = false;
            fetching = _load.Fetching;
            _searchQuery = query;
            _searchError = _search.Error;
            if (_searchError is not null)
            {
                _searchResults = [];
            }
        }

        _search.Publish(search, () => IsLoading = fetching);
        _search.Publish(search, () => RaiseItemsChanged());
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
                .Select(r => new RepoItem(this, r, _browser, now, account)));
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
        long revision;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            repositoryPages = TakeRepositoryPages();
            _accountGeneration++;
            _load.Invalidate(reset: true);
            _mine.Clear();
            _search.Invalidate(reset: true);
            _searching = false;
            _searchQuery = string.Empty;
            _searchResults = [];
            _searchError = null;
            revision = _load.Revision;
        }

        foreach (var repositoryPage in repositoryPages)
        {
            repositoryPage.Reset();
        }

        _load.Publish(revision, () => HasMoreItems = false);
        _load.Publish(revision, () => IsLoading = false);
        _load.Publish(revision, () => RaiseItemsChanged());
    }

    private RepositoryPage[] TakeRepositoryPages()
    {
        var pages = _repositoryPages.Values.Select(reference => reference.TryGetTarget(out var page) ? page : null)
            .OfType<RepositoryPage>().ToArray();
        _repositoryPages.Clear();
        return pages;
    }
}
