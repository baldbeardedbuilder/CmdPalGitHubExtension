// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class RepositoryIssuesPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.repository-issues";

    private readonly AuthService _auth;
    private readonly IIssuesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly IssueFilters _filters = new();
    private readonly Lock _lock = new();
    private readonly List<RepositoryIssueItem> _items = [];
    private string? _repository;
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;

    public RepositoryIssuesPage(AuthService auth, IIssuesClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Issues, new RefreshRepositoryItemsCommand(RefreshAsync, Icons.Issues));
        Id = PageId;
        Name = "Issues";
        Title = "Issues";
        Icon = Icons.Issues;
        PlaceholderText = "Filter issues...";
        _filters.CurrentFilterId = IssueFilters.Open;
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

    internal RepositoryIssuesPage ForRepository(string repository) =>
        new(_auth, _client, _browser, _time)
        {
            Id = $"{PageId}.{Uri.EscapeDataString(repository)}",
            Title = $"{repository} issues",
            _repository = repository,
        };

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

        Title = $"{repository} issues";
        SearchText = string.Empty;
        HasMoreItems = false;
        StartLoad(reset: true);
        RaiseItemsChanged();
        return CommandResult.GoToPage(new GoToPageArgs { PageId = PageId });
    }

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        RepositoryIssueItem[] snapshot;
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
            EmptyContent = Empty("Choose a repository", "Open a repository's issues from the Repos list");
            return [];
        }

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = snapshot
            .Where(item => filter == IssueFilters.Closed
                ? item.Issue.State is SubjectState.Closed or SubjectState.NotPlanned
                : item.Issue.State is SubjectState.Open)
            .Where(item => terms.Length == 0 || item.Matches(terms))
            .ToArray();
        var status = filter == IssueFilters.Closed ? "closed" : "open";

        EmptyContent = error is not null
            ? Empty("Couldn't load issues", error, refresh: true)
            : matches.Length == 0
                ? Empty(terms.Length == 0 ? "No issues found" : "No matching issues", terms.Length == 0
                    ? $"{repository} doesn't have any {status} issues"
                    : $"Nothing matches \"{SearchText.Trim()}\"")
                : Empty("No issues found", $"{repository} doesn't have any {status} issues");

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
            var result = await _client.GetIssuesAsync(account, repository, page, CancellationToken.None).ConfigureAwait(false);
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

                var known = _items.Select(item => item.Issue.Number).ToHashSet();
                _items.AddRange(result.Issues
                    .Where(issue => known.Add(issue.Number))
                    .Select(issue => new RepositoryIssueItem(issue, repository, _browser, now)));
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

internal sealed partial class IssueFilters : Filters
{
    internal const string Open = "open";
    internal const string Closed = "closed";

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = Open, Name = "Open" },
        new Filter { Id = Closed, Name = "Closed" },
    ];
}

internal sealed partial class RepositoryIssueItem : ListItem
{
    public RepositoryIssueItem(GitHubIssue issue, string repository, IBrowserLauncher browser, DateTimeOffset now)
    {
        Issue = issue;
        Command = new OpenInBrowserCommand(browser, issue.WebUrl, "Open in browser", Icons.Issues);
        Title = $"#{issue.Number} {issue.Title}";
        Details = new IssueDetails(issue, repository);
        var opened = $"opened {NotificationFormatting.RelativeTime(issue.CreatedAt, now)}";
        if (!string.IsNullOrWhiteSpace(issue.Author))
        {
            opened += $" by {issue.Author}";
        }

        Subtitle = $"{opened} · {issue.Comments} {(issue.Comments == 1 ? "comment" : "comments")}";
        Icon = issue.State switch
        {
            SubjectState.Open => Icons.StateOpenIssue,
            SubjectState.Closed => Icons.StateClosedIssue,
            SubjectState.NotPlanned => Icons.StateNotPlanned,
            _ => Icons.Issues,
        };
        Tags = [.. issue.Labels.Select(label => new Tag(label))];
        MoreCommands =
        [
            new CommandContextItem(new OpenInBrowserCommand(browser, issue.WebUrl, "Open in browser", Icons.Issues)),
            new CommandContextItem(new CopyTextCommand(issue.WebUrl.AbsoluteUri) { Name = "Copy link", Icon = Icons.Copy }),
        ];
    }

    public GitHubIssue Issue { get; }

    public bool Matches(string[] terms)
    {
        var searchable = string.Join(' ', Title, Subtitle, string.Join(' ', Issue.Assignees), string.Join(' ', Issue.Labels));
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
