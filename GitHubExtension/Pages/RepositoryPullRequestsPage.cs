// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class RepositoryPullRequestsPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.repository-pull-requests";

    private readonly AuthService _auth;
    private readonly IPullRequestsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly IPullRequestMergeClient? _mergeClient;
    private readonly List<MergePullRequestPage> _mergePages = [];
    private readonly PageEmptyContent _emptyContent;
    private readonly PullRequestFilters _filters = new();
    private readonly Lock _lock = new();
    private readonly List<RepositoryPullRequestItem> _items = [];
    private string? _repository;
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private volatile bool _disposed;
    private string? _error;
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private Task _currentLoad = Task.CompletedTask;

    public RepositoryPullRequestsPage(
        AuthService auth,
        IPullRequestsClient client,
        IBrowserLauncher browser,
        TimeProvider? time = null,
        IPullRequestMergeClient? mergeClient = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _mergeClient = mergeClient;
        _emptyContent = new PageEmptyContent(Icons.PullRequests, new RefreshRepositoryItemsCommand(RefreshAsync, Icons.PullRequests));
        Id = PageId;
        Name = "Pull requests";
        Title = "Pull requests";
        Icon = Icons.PullRequests;
        PlaceholderText = "Filter pull requests...";
        _filters.CurrentFilterId = PullRequestFilters.Open;
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

    internal RepositoryPullRequestsPage ForRepository(string repository) =>
        new(_auth, _client, _browser, _time, _mergeClient)
        {
            Id = $"{PageId}.{Uri.EscapeDataString(repository)}",
            Title = $"{repository} pull requests",
            _repository = repository,
        };

    internal ICommandResult Open(string repository)
    {
        MergePullRequestPage[] mergePages;
        lock (_lock)
        {
            if (_disposed)
            {
                return CommandResult.KeepOpen();
            }

            CancelLoad();
            mergePages = TakeMergePages();
            _repository = repository;
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
            _items.Clear();
        }

        DisposeMergePages(mergePages);
        Title = $"{repository} pull requests";
        SearchText = string.Empty;
        HasMoreItems = false;
        StartLoad(reset: true);
        RaiseItemsChanged();
        return CommandResult.GoToPage(new GoToPageArgs { PageId = PageId });
    }

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        RepositoryPullRequestItem[] snapshot;
        string? repository;
        string? error;
        string filter;
        lock (_lock)
        {
            needsLoad = _repository is not null && !_loaded && !_fetching;
            snapshot = [.. _items];
            repository = _repository;
            error = _error;
            filter = _filters.CurrentFilterId;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        if (repository is null)
        {
            EmptyContent = Empty("Choose a repository", "Open a repository's pull requests from the Repos list");
            return [];
        }

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot
            .Where(item => MatchesFilter(item.PullRequest.State, filter))
            .Where(item => terms.Length == 0 || item.Matches(terms))
            .ToArray();
        var emptyStatus = filter switch
        {
            PullRequestFilters.Open => "open",
            PullRequestFilters.Closed => "closed",
            _ => null,
        };

        EmptyContent = error is not null
            ? Empty("Couldn't load pull requests", error, refresh: true)
            : matches.Length == 0
                ? Empty(
                    terms.Length == 0 && emptyStatus is null ? "No pull requests found" : "No matching pull requests",
                    terms.Length == 0
                        ? emptyStatus is null
                            ? $"{repository} doesn't have any pull requests"
                            : $"{repository} doesn't have any {emptyStatus} pull requests"
                        : $"Nothing matches \"{SearchText.Trim()}\"")
                : Empty("No pull requests found", $"{repository} doesn't have any pull requests");

        return matches;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        bool hasMore;
        lock (_lock)
        {
            hasMore = newSearch.Trim().Length == 0 && _nextPage is not null;
        }

        HasMoreItems = hasMore;
        RaiseItemsChanged();
    }

    public override void LoadMore()
    {
        if (SearchText.Trim().Length == 0)
        {
            StartLoad(reset: false);
        }
    }

    internal Task RefreshAsync()
    {
        MergePullRequestPage[] mergePages;
        lock (_lock)
        {
            if (_disposed)
            {
                return _currentLoad;
            }

            if (_repository is null)
            {
                return _currentLoad;
            }

            CancelLoad();
            mergePages = TakeMergePages();
            _loaded = false;
            _nextPage = null;
            _error = null;
            _items.Clear();
        }

        DisposeMergePages(mergePages);
        HasMoreItems = false;
        return StartLoad(reset: true);
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        MergePullRequestPage[] mergePages;
        lock (_lock)
        {
            _disposed = true;
            CancelLoad();
            mergePages = TakeMergePages();
        }

        DisposeMergePages(mergePages);
        IsLoading = false;
        HasMoreItems = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    private static bool MatchesFilter(SubjectState state, string filter) =>
        filter switch
        {
            PullRequestFilters.Open => state is SubjectState.Open or SubjectState.Draft,
            PullRequestFilters.Closed => state is SubjectState.Closed or SubjectState.Merged,
            _ => true,
        };

    private Task StartLoad(bool reset)
    {
        GitHubAccount? account;
        string? repository;
        Uri? page;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            repository = _repository;
            if (_disposed || account is null || repository is null || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            token = _loadCts.Token;
            _fetching = true;
            page = reset ? null : _nextPage;
            generation = _generation;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation || _disposed)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, repository, page, reset, generation, token));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, string repository, Uri? page, bool reset, int generation, CancellationToken token)
    {
        try
        {
            var result = await _client.GetPullRequestsAsync(account, repository, page, token).ConfigureAwait(false);
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

                var known = _items.Select(item => item.PullRequest.Number).ToHashSet();
                _items.AddRange(result.PullRequests
                    .Where(pullRequest => known.Add(pullRequest.Number))
                    .Select(pullRequest =>
                    {
                        MergePullRequestPage? mergePage = null;
                        if (_mergeClient is not null && pullRequest.State == SubjectState.Open)
                        {
                            mergePage = new MergePullRequestPage(_auth, _mergeClient, account, repository, pullRequest.Number, pullRequest.WebUrl);
                            _mergePages.Add(mergePage);
                        }

                        return new RepositoryPullRequestItem(pullRequest, _browser, now, mergePage);
                    }));
                _nextPage = result.NextPage;
                _loaded = true;
                _error = null;
                hasMore = result.NextPage is not null && SearchText.Trim().Length == 0;
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
        }
        finally
        {
            bool current;
            lock (_lock)
            {
                current = generation == _generation && !_disposed;
                if (current)
                {
                    _fetching = false;
                }
            }

            if (current)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private void Reset()
    {
        MergePullRequestPage[] mergePages;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelLoad();
            mergePages = TakeMergePages();
            _repository = null;
            _items.Clear();
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
        }

        DisposeMergePages(mergePages);
        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _fetching = false;
    }

    private MergePullRequestPage[] TakeMergePages()
    {
        var pages = _mergePages.ToArray();
        _mergePages.Clear();
        return pages;
    }

    private static void DisposeMergePages(MergePullRequestPage[] pages)
    {
        foreach (var page in pages) page.Dispose();
    }
}

internal sealed partial class PullRequestFilters : Filters
{
    internal const string Open = "open";
    internal const string Closed = "closed";

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = Open, Name = "Open", Icon = Icons.StateOpenPullRequest },
        new Filter { Id = Closed, Name = "Closed", Icon = Icons.StateClosedPullRequest },
    ];
}

internal sealed partial class RepositoryPullRequestItem : ListItem
{
    public RepositoryPullRequestItem(GitHubPullRequest pullRequest, IBrowserLauncher browser, DateTimeOffset now, MergePullRequestPage? mergePage = null)
    {
        PullRequest = pullRequest;
        Command = new OpenInBrowserCommand(browser, pullRequest.WebUrl, "Open in browser", Icons.PullRequests);
        Title = $"#{pullRequest.Number} {pullRequest.Title}";

        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(pullRequest.HeadRef) && !string.IsNullOrWhiteSpace(pullRequest.BaseRef))
        {
            metadata.Add($"{pullRequest.HeadRef} → {pullRequest.BaseRef}");
        }

        var opened = NotificationFormatting.RelativeTime(pullRequest.CreatedAt, now);
        if (!string.IsNullOrWhiteSpace(pullRequest.Author))
        {
            opened += $" by {pullRequest.Author}";
        }

        metadata.Add(opened);
        Subtitle = string.Join(" · ", metadata);
        Icon = pullRequest.State switch
        {
            SubjectState.Open => Icons.StateOpenPullRequest,
            SubjectState.Draft => Icons.StateDraft,
            SubjectState.Merged => Icons.StateMerged,
            SubjectState.Closed => Icons.StateClosedPullRequest,
            _ => Icons.PullRequests,
        };
        Tags = NotificationFormatting.StateTag("PullRequest", pullRequest.State) is { } state ? [state] : [];
        var commands = new List<IContextItem>
        {
            new CommandContextItem(new OpenInBrowserCommand(browser, pullRequest.WebUrl, "Open in browser", Icons.PullRequests)),
            new CommandContextItem(new CopyTextCommand(pullRequest.WebUrl.AbsoluteUri) { Name = "Copy link", Icon = Icons.Copy }),
        };
        if (mergePage is not null) commands.Add(new CommandContextItem(mergePage));
        MoreCommands = [.. commands];
    }

    public GitHubPullRequest PullRequest { get; }

    public bool Matches(string[] terms)
    {
        var searchable = string.Join(' ', Title, Subtitle, string.Join(' ', PullRequest.Labels), PullRequest.State.ToString());
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
