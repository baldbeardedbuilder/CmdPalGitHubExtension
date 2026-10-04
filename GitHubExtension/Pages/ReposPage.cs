// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using BaldBeardedBuilder.CmdPal.GitHub.Search;

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
    private readonly WorkItemDetailFactories? _workItemDetailFactories;
    private readonly IIssueSearchClient? _issueSearchClient;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly PageListContent _searchFailureContent;
    private readonly PagedListPresentation _searchPagination;
    private readonly TimeSpan _searchDelay;
    private readonly ListLoadState _load = new();
    private readonly ListLoadState _searchLoad;
    private Lock _lock => _load.SyncRoot;
    private readonly List<RepoItem> _mine = [];
    private readonly Dictionary<string, WeakReference<RepositoryPage>> _repositoryPages = new(StringComparer.OrdinalIgnoreCase);

    private string _searchQuery = string.Empty;
    private List<RepoItem> _searchResults = [];
    private int? _searchTotalCount;
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
        IAgentsClient? agentsClient = null,
        IRepositoryStarsClient? starsClient = null,
        bool starred = false,
        WorkItemDetailFactories? workItemDetailFactories = null,
        IIssueSearchClient? issueSearchClient = null,
        ICodespacesClient? codespacesClient = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _repositoryIssuesPage = repositoryIssuesPage;
        _repositoryPullRequestsPage = repositoryPullRequestsPage;
        _agentsClient = agentsClient;
        _workItemDetailFactories = workItemDetailFactories;
        _issueSearchClient = issueSearchClient ?? client as IIssueSearchClient;
        CodespacesClient = codespacesClient;
        _starsClient = starsClient ?? client as IRepositoryStarsClient;
        _starred = starred;
        if (starred && _starsClient is null)
        {
            throw new ArgumentException("The starred view needs a stars client.", nameof(starsClient));
        }

        _starExecutor = new MutationExecutor(auth);
        _time = time ?? TimeProvider.System;
        _searchLoad = new ListLoadState(_lock);
        _emptyContent = new PageEmptyContent(Icons.Repos, new RefreshReposCommand(this));
        _searchFailureContent = new PageListContent(Icons.Repos, new RefreshReposCommand(this));
        _searchPagination = new PagedListPresentation(Icons.Repos, () => StartSearch(reset: false));
        _searchDelay = searchDelay ?? DefaultSearchDelay;
        Actions = actions;
        Id = starred ? "" : PinDestination.GlobalId(PinDestinationKind.Repos);
        Name = "Open";
        Title = starred ? "Starred repositories" : "Repos";
        Icon = Icons.Repos;
        PlaceholderText = starred ? "Filter starred repositories..." : "Filter repos and search GitHub...";
        _auth.AccountChanged += OnAccountChanged;
    }

    internal ActionsPage? Actions { get; }
    internal ICodespacesClient? CodespacesClient { get; }

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
                return _searchLoad.CurrentLoad;
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
        Uri? searchNextPage;
        int? searchTotalCount;
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
            searchError = _searchLoad.Error;
            searching = _searchLoad.Fetching;
            searchQuery = _searchQuery;
            searchNextPage = _searchLoad.NextPage;
            searchTotalCount = _searchTotalCount;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        var query = SearchText.Trim();
        if (_starred)
        {
            return StarredItems(mine, query, error);
        }

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

        var partialSearch = searchQuery == query && (searchNextPage is not null || searchTotalCount > remote.Length);
        EmptyContent = searching
            ? Empty("Searching GitHub...", string.Empty)
            : searchError is not null
                ? Empty("Couldn't search GitHub", searchError, refresh: true)
                : partialSearch
                    ? Empty("No loaded repos found", "More repository search results may be available")
                : Empty("No repos found", $"Nothing matches \"{query}\"");

        if (searchQuery == query && (partialSearch || searching))
        {
            var scope = searchTotalCount is { } total
                ? $"Showing {remote.Length} of {total} GitHub results, plus matching loaded personal repos."
                : "Filtering loaded personal repos while GitHub search runs.";
            var limited = searchTotalCount > RepositoriesClient.SearchResultLimit;
            if (limited)
            {
                scope = $"Narrow your search to find more. {scope}";
            }

            return _searchPagination.Append([.. local],
                limited ? "GitHub search is limited to 1,000 results" : searching ? "Searching GitHub..." : "Repository search results",
                scope, searchNextPage is not null, searching, searchError);
        }

        if (searchError is not null && local.Count > 0)
        {
            return [.. local, _searchFailureContent.Get("Couldn't search GitHub", searchError)];
        }

        return [.. local];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        var query = newSearch.Trim();
        if (query == oldSearch.Trim())
        {
            return;
        }

        bool hasMore;
        bool fetching;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _searchLoad.Invalidate(reset: true);
            _searchQuery = query;
            _searchResults = [];
            _searchTotalCount = null;
            hasMore = _load.NextPage is not null;
            fetching = _load.Fetching;
        }

        HasMoreItems = query.Length == 0 && hasMore;
        if (_starred)
        {
            HasMoreItems = hasMore;
            RaiseItemsChanged();
            return;
        }

        if (query.Length == 0)
        {
            IsLoading = fetching;
        }
        else
        {
            StartSearch(reset: true);
        }

        RaiseItemsChanged();
    }

    public override void LoadMore()
    {
        if (_starred || SearchText.Trim().Length == 0)
        {
            StartLoad(reset: false);
        }
        else
        {
            StartSearch(reset: false);
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
        RepositoryStarPage[] starPages;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            _searchLoad.Dispose();
            repositoryPages = TakeRepositoryPages();
            starPages = TakeStarPages();
            _mine.Clear();
            _searchResults.Clear();
        }

        foreach (var page in repositoryPages)
        {
            page.Dispose();
        }

        foreach (var page in starPages)
        {
            page.Dispose();
        }

        _starExecutor.Dispose();
        DisposeBrowsingPages();
        IsLoading = false;
        HasMoreItems = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false)
    {
        var empty = _emptyContent.Get(title, subtitle, refresh);
        empty.MoreCommands = WorkSearchCommands();
        return empty;
    }

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

            var page = new RepositoryPage(_browser, Actions, repository, _repositoryIssuesPage, _repositoryPullRequestsPage,
                _auth, _agentsClient, StarPage(repository, _auth.CurrentAccount, _accountGeneration),
                WatchPage(repository, _auth.CurrentAccount, _accountGeneration),
                RepositoryAgents(repository.FullName, _auth.CurrentAccount, _accountGeneration),
                CodespacesClient);
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

    private Task StartSearch(bool reset)
    {
        GitHubAccount? account;
        string query;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            query = _searchQuery;
            if (account is null || query.Length == 0 || query != SearchText.Trim()
                || !_searchLoad.TryBegin(reset, out operation))
            {
                return _searchLoad.CurrentLoad;
            }
        }

        _searchLoad.Publish(operation, () => IsLoading = true);
        return _searchLoad.Run(operation, () => SearchAsync(account, query, operation), () => PublishSearch(operation),
            "GitHub took too long to respond. Try searching again.", area: DiagnosticArea.Repositories,
            diagnosticEvent: DiagnosticEvent.PageSearch);
    }

    private async Task SearchAsync(GitHubAccount account, string query, ListLoadState.Operation operation)
    {
        if (operation.Reset)
        {
            await Task.Delay(_searchDelay, operation.Token).ConfigureAwait(false);
        }

        var results = await _client.SearchAsync(account, query, operation.Page, operation.Token).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_searchLoad.IsCurrent(operation))
            {
                return;
            }

            if (operation.Reset)
            {
                _searchResults.Clear();
            }

            var seen = _searchResults.Select(item => item.Repository.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _searchResults.AddRange(results.Repositories.Where(r => seen.Add(r.FullName))
                .Select(r => new RepoItem(this, r, _browser, now, account)));
            _searchTotalCount = results.TotalCount;
            _searchLoad.Succeed(operation, results.NextPage);
        }
    }

    private void PublishSearch(ListLoadState.Operation operation)
    {
        bool fetching;
        lock (_lock)
        {
            fetching = _load.Fetching;
        }

        _searchLoad.Publish(operation, () => IsLoading = fetching);
        _searchLoad.Publish(operation, () => RaiseItemsChanged());
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
        var result = _starred
            ? await _starsClient!.GetStarredAsync(account, operation.Page, operation.Token).ConfigureAwait(false)
            : await _client.GetMyRepositoriesAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
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
            hasMore = _load.NextPage is not null && (_starred || SearchText.Trim().Length == 0);
            searching = _searchLoad.Fetching;
        }

        _load.Publish(operation, () => HasMoreItems = hasMore);
        _load.Publish(operation, () => IsLoading = searching);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void Reset()
    {
        RepositoryPage[] repositoryPages;
        RepositoryStarPage[] starPages;
        long revision;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            repositoryPages = TakeRepositoryPages();
            starPages = TakeStarPages();
            _accountGeneration++;
            _load.Invalidate(reset: true);
            _mine.Clear();
            _searchLoad.Invalidate(reset: true);
            _searchQuery = string.Empty;
            _searchResults = [];
            _searchTotalCount = null;
            revision = _load.Revision;
        }

        foreach (var repositoryPage in repositoryPages)
        {
            repositoryPage.Reset();
        }

        foreach (var page in starPages)
        {
            page.Dispose();
        }

        DisposeBrowsingPages();
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
