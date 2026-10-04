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
    private readonly IDisposable _accountSubscription;
    private readonly IIssuesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly PagedListPresentation _pagination;
    private readonly IssueFilters _filters = new();
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<RepositoryIssueItem> _items = [];
    private readonly List<IssueDetailsPage> _detailsPages = [];
    private readonly List<IDisposable> _auxiliaryPages = [];
    private IssueWritePage? _createIssuePage;
    private string? _repository;

    public RepositoryIssuesPage(AuthService auth, IIssuesClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Issues, new RefreshRepositoryItemsCommand(RefreshAsync, Icons.Issues));
        _pagination = new PagedListPresentation(Icons.Issues, () => StartLoad(reset: false));
        Name = "Issues";
        Title = "Issues";
        Icon = Icons.Issues;
        PlaceholderText = "Filter loaded issues...";
        _filters.CurrentFilterId = IssueFilters.Open;
        _filters.PropChanged += (_, _) => RaiseItemsChanged();
        Filters = _filters;
        _accountSubscription = auth.Subscribe(this, static page => page.OnAccountChanged(null, EventArgs.Empty));
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

    internal RepositoryPage? Owner { get; private init; }

    internal RepositoryIssuesPage ForRepository(string repository, RepositoryPage? owner = null, AuthService? auth = null)
    {
        var pageAuth = auth ?? _auth;
        var page = new RepositoryIssuesPage(pageAuth, _client, _browser, _time)
        {
            Id = PinDestination.RepositoryId(PinDestinationKind.RepositoryIssues, pageAuth.CurrentAccount, repository),
            Title = $"{repository} issues",
            _repository = repository,
            Owner = owner,
        };
        page._createIssuePage = page.CreateIssueWritePage(repository);
        return page;
    }

    internal ICommandResult Open(string repository)
    {
        IssueDetailsPage[] retired;
        IDisposable[] retiredAuxiliary;
        IssueWritePage? retiredCreate;
        lock (_lock)
        {
            retired = [.. _detailsPages];
            _detailsPages.Clear();
            retiredAuxiliary = [.. _auxiliaryPages];
            _auxiliaryPages.Clear();
            retiredCreate = _createIssuePage;
            _createIssuePage = CreateIssueWritePage(repository);
            _load.Invalidate(reset: true);
            _repository = repository;
            _items.Clear();
        }

        foreach (var page in retired)
        {
            page.Dispose();
        }
        foreach (var page in retiredAuxiliary) page.Dispose();
        retiredCreate?.Dispose();

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

        var partial = hasMore || !loaded;
        EmptyContent = error is not null
            ? Empty("Couldn't load issues", error, refresh: true)
            : loading
                ? Empty("Loading issues...", "Filtering loaded results")
            : matches.Length == 0
                ? Empty(partial ? "No matching loaded issues" : terms.Length == 0 ? "No issues found" : "No matching issues", partial
                    ? $"No loaded {status} issues match. More issues may be available."
                    : terms.Length == 0
                    ? $"{repository} doesn't have any {status} issues"
                    : $"Nothing matches \"{SearchText.Trim()}\"")
                : Empty("No issues found", $"{repository} doesn't have any {status} issues");

        IListItem[] loadedItems = hasMore || loading || (error is not null && snapshot.Length > 0)
            ? _pagination.Append(matches, loading ? "Loading issues..." : "Filtering loaded issues",
                $"{matches.Length} matching {status} issues in {snapshot.Length} loaded issues. More issues may be available.",
                hasMore, loading, error)
            : matches;
        if (_createIssuePage is not { } createIssue)
        {
            return loadedItems;
        }

        var result = new List<IListItem>(loadedItems.Length + 1)
        {
            new ListItem(createIssue)
            {
                Title = "Create an issue",
                Subtitle = "Write an issue with a title, description, and milestone",
                Icon = Icons.Issues,
            },
        };
        result.AddRange(loadedItems);
        return [.. result];
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
        IssueDetailsPage[] retired;
        IDisposable[] auxiliary;
        lock (_lock)
        {
            if (_repository is null)
            {
                return _load.CurrentLoad;
            }

            _load.Invalidate(reset: true);
            _items.Clear();
            retired = [.. _detailsPages];
            _detailsPages.Clear();
            auxiliary = [.. _auxiliaryPages];
            _auxiliaryPages.Clear();
        }

        foreach (var page in retired)
        {
            page.Dispose();
        }
        foreach (var page in auxiliary) page.Dispose();

        HasMoreItems = false;
        return StartLoad(reset: true);
    }

    private IssueWritePage? CreateIssueWritePage(string repository) =>
        _client is IIssueManagementClient management
            ? new IssueWritePage(_auth, management, repository, created: IssueCreatedAsync) { Owner = this }
            : null;

    private async Task IssueCreatedAsync(GitHubAccount account)
    {
        IssueWritePage? completedPage;
        lock (_lock)
        {
            if (_repository is not { } repository || _load.Disposed || !ReferenceEquals(account, _auth.CurrentAccount)) return;
            completedPage = _createIssuePage;
            _createIssuePage = CreateIssueWritePage(repository);
        }

        await RefreshAsync().ConfigureAwait(false);

        var dispose = false;
        lock (_lock)
        {
            if (completedPage is not null)
            {
                dispose = _load.Disposed || !ReferenceEquals(account, _auth.CurrentAccount);
                if (!dispose) _auxiliaryPages.Add(completedPage);
            }
        }
        if (dispose) completedPage?.Dispose();
        else RaiseItemsChanged();
    }

    public void Dispose()
    {
        _accountSubscription.Dispose();
        IssueDetailsPage[] retired;
        IDisposable[] auxiliary;
        IssueWritePage? create;
        lock (_lock)
        {
            _load.Dispose();
            retired = [.. _detailsPages];
            _detailsPages.Clear();
            auxiliary = [.. _auxiliaryPages];
            _auxiliaryPages.Clear();
            create = _createIssuePage;
            _createIssuePage = null;
        }

        foreach (var page in retired)
        {
            page.Dispose();
        }
        foreach (var page in auxiliary) page.Dispose();
        create?.Dispose();

        IsLoading = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false) =>
        _emptyContent.Get(title, subtitle, refresh);

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
            "GitHub took too long to respond. Try refreshing issues.", area: DiagnosticArea.Issues);
    }

    private async Task LoadAsync(GitHubAccount account, string repository, ListLoadState.Operation operation)
    {
        var result = await _client.GetIssuesAsync(account, repository, operation.Page, operation.Token).ConfigureAwait(false);
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

            var known = _items.Select(item => item.Issue.Number).ToHashSet();
            _items.AddRange(result.Issues
                .Where(issue => known.Add(issue.Number))
                .Select(issue =>
                {
                    var details = IssueDetailsPage.ForIssue(_auth, _client, _browser, account,
                        IssuesClient.IssueUri(account, repository, issue.Number), repository,
                        (source, updated) => ApplyIssueUpdate(account, repository, source, updated));
                    _detailsPages.Add(details);
                    IssueWritePage? editor = null;
                    IssueConversationPage? conversation = null;
                    if (_client is IIssueManagementClient issueManagement)
                    {
                        editor = new IssueWritePage(_auth, issueManagement, repository, issue) { Owner = this };
                        _auxiliaryPages.Add(editor);
                    }

                    if (_client is IIssueConversationClient conversationClient)
                    {
                        conversation = new IssueConversationPage(_auth, conversationClient, account, repository, issue.Number, "Issue",
                            icon: Icons.SubjectIcon(false, issue.State));
                        _auxiliaryPages.Add(conversation);
                    }

                    return new RepositoryIssueItem(issue, repository, _browser, now, details, editor, conversation);
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
        IssueDetailsPage[] retired;
        IDisposable[] auxiliary;
        IssueWritePage? create;
        lock (_lock)
        {
            _load.Invalidate(reset: true);
            _repository = null;
            _items.Clear();
            retired = [.. _detailsPages];
            _detailsPages.Clear();
            auxiliary = [.. _auxiliaryPages];
            _auxiliaryPages.Clear();
            create = _createIssuePage;
            _createIssuePage = null;
        }

        foreach (var page in retired)
        {
            page.Dispose();
        }
        foreach (var page in auxiliary) page.Dispose();
        create?.Dispose();

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Reset();

    private void ApplyIssueUpdate(GitHubAccount account, string repository, IssueDetailsPage source, GitHubIssue updated)
    {
        IssueConversationPage? conversation;
        lock (_lock)
        {
            var index = _items.FindIndex(item => ReferenceEquals(item.Command, source));
            if (_load.Disposed || !ReferenceEquals(account, _auth.CurrentAccount) || _repository != repository || index < 0)
            {
                return;
            }

            var existing = _items[index];
            var item = new RepositoryIssueItem(updated, repository, _browser, _time.GetUtcNow(), source,
                existing.Editor, existing.ConversationPage);
            _load.Invalidate();
            _items[index] = item;
            conversation = existing.ConversationPage;
        }

        if (conversation is not null) conversation.Icon = Icons.SubjectIcon(false, updated.State);
        IsLoading = false;
        RaiseItemsChanged();
    }
}

internal sealed partial class IssueFilters : Filters
{
    internal const string Open = "open";
    internal const string Closed = "closed";

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = Open, Name = "Open", Icon = Icons.StateOpenIssue },
        new Filter { Id = Closed, Name = "Closed", Icon = Icons.StateClosedIssue },
    ];
}

internal sealed partial class RepositoryIssueItem : ListItem
{
    public RepositoryIssueItem(GitHubIssue issue, string repository, IBrowserLauncher browser, DateTimeOffset now,
        IssueDetailsPage? details = null, IssueWritePage? editor = null, IssueConversationPage? conversation = null)
    {
        Issue = issue;
        Editor = editor;
        ConversationPage = conversation;
        Command = details is null ? new OpenInBrowserCommand(browser, issue.WebUrl, "Open in browser", Icons.Issues) : details;
        Title = $"#{issue.Number} {issue.Title}";
        Details = new IssueDetails(issue, repository);
        var opened = $"opened {NotificationFormatting.RelativeTime(issue.CreatedAt, now)}";
        if (!string.IsNullOrWhiteSpace(issue.Author))
        {
            opened += $" by {issue.Author}";
        }

        Subtitle = $"{opened} · {issue.Comments} {(issue.Comments == 1 ? "comment" : "comments")}";
        Icon = Icons.SubjectIcon(false, issue.State);
        Tags = [.. issue.Labels.Select(label => new Tag(label))];
        var commands = new List<IContextItem>
        {
            new CommandContextItem(new OpenInBrowserCommand(browser, issue.WebUrl, "Open in browser", Icons.Issues)),
            new CommandContextItem(new CopyTextCommand(issue.WebUrl.AbsoluteUri) { Name = "Copy link", Icon = Icons.Copy }),
        };
        if (editor is not null) commands.Add(new CommandContextItem(editor));
        if (conversation is not null) commands.Add(new CommandContextItem(conversation));
        MoreCommands = [.. commands];
    }

    public GitHubIssue Issue { get; }
    internal IssueWritePage? Editor { get; }
    internal IssueConversationPage? ConversationPage { get; }

    public bool Matches(string[] terms)
    {
        var searchable = string.Join(' ', Title, Subtitle, string.Join(' ', Issue.Assignees), string.Join(' ', Issue.Labels));
        return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
