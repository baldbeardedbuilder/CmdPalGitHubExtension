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
    private readonly PageEmptyContent _emptyContent;
    private readonly Lock _lock = new();
    private readonly List<RepositoryPullRequestItem> _items = [];
    private string? _repository;
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;

    public RepositoryPullRequestsPage(
        AuthService auth,
        IPullRequestsClient client,
        IBrowserLauncher browser,
        TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.PullRequests, new RefreshRepositoryItemsCommand(RefreshAsync, Icons.PullRequests));
        Id = PageId;
        Name = "Pull requests";
        Title = "Pull requests";
        Icon = Icons.PullRequests;
        PlaceholderText = "Filter pull requests...";
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

    internal ICommandResult Open(string repository)
    {
        lock (_lock)
        {
            _generation++;
            _repository = repository;
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
            _items.Clear();
        }

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
        lock (_lock)
        {
            needsLoad = _repository is not null && !_loaded && !_fetching;
            snapshot = [.. _items];
            repository = _repository;
            error = _error;
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
        var matches = terms.Length == 0
            ? snapshot
            : [.. snapshot.Where(item => item.Matches(terms))];

        EmptyContent = error is not null
            ? Empty("Couldn't load pull requests", error, refresh: true)
            : matches.Length == 0
                ? Empty(terms.Length == 0 ? "No pull requests found" : "No matching pull requests", terms.Length == 0
                    ? $"{repository} doesn't have any pull requests"
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
        lock (_lock)
        {
            if (_repository is null)
            {
                return _currentLoad;
            }

            _generation++;
            _fetching = false;
            _loaded = false;
            _nextPage = null;
            _error = null;
            _items.Clear();
        }

        HasMoreItems = false;
        return StartLoad(reset: true);
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

    private Task StartLoad(bool reset)
    {
        GitHubAccount? account;
        string? repository;
        Uri? page;
        int generation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            repository = _repository;
            if (account is null || repository is null || _fetching || (!reset && _nextPage is null))
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

            _currentLoad = Task.Run(() => LoadAsync(account, repository, page, reset, generation));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, string repository, Uri? page, bool reset, int generation)
    {
        try
        {
            var result = await _client.GetPullRequestsAsync(account, repository, page, CancellationToken.None).ConfigureAwait(false);
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

                var known = _items.Select(item => item.PullRequest.Number).ToHashSet();
                _items.AddRange(result.PullRequests
                    .Where(pullRequest => known.Add(pullRequest.Number))
                    .Select(pullRequest => new RepositoryPullRequestItem(pullRequest, _browser, now)));
                _nextPage = result.NextPage;
                _loaded = true;
                _error = null;
                hasMore = result.NextPage is not null && SearchText.Trim().Length == 0;
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
        }
        finally
        {
            bool current;
            lock (_lock)
            {
                current = generation == _generation;
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
        lock (_lock)
        {
            _generation++;
            _repository = null;
            _items.Clear();
            _nextPage = null;
            _loaded = false;
            _fetching = false;
            _error = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();
}

internal sealed partial class RepositoryPullRequestItem : ListItem
{
    public RepositoryPullRequestItem(GitHubPullRequest pullRequest, IBrowserLauncher browser, DateTimeOffset now)
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
        MoreCommands =
        [
            new CommandContextItem(new OpenInBrowserCommand(browser, pullRequest.WebUrl, "Open in browser", Icons.PullRequests)),
            new CommandContextItem(new CopyTextCommand(pullRequest.WebUrl.AbsoluteUri) { Name = "Copy link", Icon = Icons.Copy }),
        ];
    }

    public GitHubPullRequest PullRequest { get; }

    public bool Matches(string[] terms)
    {
        var searchable = string.Join(' ', Title, Subtitle, string.Join(' ', PullRequest.Labels), PullRequest.State.ToString());
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
