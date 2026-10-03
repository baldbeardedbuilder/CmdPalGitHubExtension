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
    private readonly PagedListPresentation _pagination;
    private readonly PullRequestFilters _filters = new();
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<RepositoryPullRequestItem> _items = [];
    private string? _repository;

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
        _pagination = new PagedListPresentation(Icons.PullRequests, () => StartLoad(reset: false));
        Id = PageId;
        Name = "Pull requests";
        Title = "Pull requests";
        Icon = Icons.PullRequests;
        PlaceholderText = "Filter loaded pull requests...";
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
                return _load.CurrentLoad;
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
            _load.Invalidate(reset: true);
            mergePages = TakeMergePages();
            _repository = repository;
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
        bool hasMore;
        bool loading;
        bool loaded;
        lock (_lock)
        {
            needsLoad = _repository is not null && _load.NeedsLoad;
            snapshot = [.. _items];
            repository = _repository;
            error = _load.Error;
            filter = _filters.CurrentFilterId;
            hasMore = _load.NextPage is not null;
            loading = _load.Fetching || needsLoad;
            loaded = _load.Loaded;
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

        var partial = hasMore || !loaded;
        EmptyContent = error is not null
            ? Empty("Couldn't load pull requests", error, refresh: true)
            : loading
                ? Empty("Loading pull requests...", "Filtering loaded results")
            : matches.Length == 0
                ? Empty(
                    partial ? "No matching loaded pull requests" : terms.Length == 0 && emptyStatus is null ? "No pull requests found" : "No matching pull requests",
                    partial
                        ? $"No loaded {emptyStatus ?? "matching"} pull requests match. More pull requests may be available."
                        : terms.Length == 0
                        ? emptyStatus is null
                            ? $"{repository} doesn't have any pull requests"
                            : $"{repository} doesn't have any {emptyStatus} pull requests"
                        : $"Nothing matches \"{SearchText.Trim()}\"")
                : Empty("No pull requests found", $"{repository} doesn't have any pull requests");

        return hasMore || loading || (error is not null && snapshot.Length > 0)
            ? _pagination.Append(matches, loading ? "Loading pull requests..." : "Filtering loaded pull requests",
                $"{matches.Length} matching {emptyStatus ?? "all"} pull requests in {snapshot.Length} loaded pull requests. More pull requests may be available.",
                hasMore, loading, error)
            : matches;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        RaiseItemsChanged();
    }

    public override void LoadMore()
    {
        StartLoad(reset: false);
    }

    internal Task RefreshAsync()
    {
        MergePullRequestPage[] mergePages;
        lock (_lock)
        {
            if (_repository is null)
            {
                return _load.CurrentLoad;
            }

            _load.Invalidate(reset: true);
            mergePages = TakeMergePages();
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
            _load.Dispose();
            mergePages = TakeMergePages();
        }

        DisposeMergePages(mergePages);
        IsLoading = false;
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
        ListLoadState.Operation operation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            repository = _repository;
            if (account is null || repository is null || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(account, repository, operation), () => PublishLoad(operation),
            "GitHub took too long to respond. Try refreshing pull requests.", area: DiagnosticArea.PullRequests);
    }

    private async Task LoadAsync(GitHubAccount account, string repository, ListLoadState.Operation operation)
    {
        var result = await _client.GetPullRequestsAsync(account, repository, operation.Page, operation.Token).ConfigureAwait(false);
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
            _load.Succeed(operation, result.NextPage);
        }
    }

    private void PublishLoad(ListLoadState.Operation operation)
    {
        _load.Publish(operation, () => HasMoreItems = false);
        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private void Reset()
    {
        MergePullRequestPage[] mergePages;
        lock (_lock)
        {
            _load.Invalidate(reset: true);
            mergePages = TakeMergePages();
            _repository = null;
            _items.Clear();
        }

        DisposeMergePages(mergePages);
        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

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
