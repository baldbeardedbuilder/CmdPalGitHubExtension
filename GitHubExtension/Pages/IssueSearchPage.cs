using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Search;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class IssueSearchPage : DynamicListPage, IDisposable
{
    internal const string PageId = "com.baldbeardedbuilder.cmdpal.github.issue-search";
    private readonly AuthService _auth;
    private readonly IIssueSearchClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly ISavedIssueQueryStore _store;
    private readonly WorkItemDetailsCache _nativeDetails;
    private readonly ListLoadState _load = new();
    private readonly PageEmptyContent _empty;
    private readonly PagedListPresentation _pagination;
    private readonly List<IssueSearchResult> _items = [];
    private IssueSearchKind _kind;
    private string _query = "";
    private int _total;
    private bool _incomplete;
    private int _generation;
    private string? _storeError;
    private BrowsingFormPage? _form;

    public IssueSearchPage(AuthService auth, IIssueSearchClient client, IBrowserLauncher browser, ISavedIssueQueryStore? store = null,
        WorkItemDetailFactories? detailFactories = null)
    {
        (_auth, _client, _browser, _store) = (auth, client, browser, store ?? new SavedIssueQueryStore());
        _nativeDetails = new(detailFactories);
        Name = "Search issues and pull requests";
        Title = Name;
        Icon = Icons.Issues;
        PlaceholderText = "Search GitHub with qualifiers...";
        ShowDetails = true;
        _empty = new(Icons.Issues, new RefreshCommand(this));
        _pagination = new(Icons.Issues, () => StartLoad(false));
        _auth.AccountChanged += AccountChanged;
    }

    internal Task CurrentLoad => _load.CurrentLoad;
    internal IssueSearchKind Kind => _kind;
    internal bool IsCurrent(GitHubAccount? account, int generation) =>
        !_load.Disposed && account is not null && _auth.CurrentAccount == account && _generation == generation;

    public override IListItem[] GetItems()
    {
        GitHubAccount? account;
        int generation;
        IssueSearchResult[] items;
        string? error;
        bool fetching;
        Uri? next;
        string query;
        int total;
        bool incomplete;
        lock (_load.SyncRoot)
        {
            if (_load.Disposed) { return []; }
            account = _auth.CurrentAccount;
            generation = _generation;
            items = [.. _items];
            error = _load.Error ?? _storeError;
            fetching = _load.Fetching;
            next = _load.NextPage;
            query = _query;
            total = _total;
            incomplete = _incomplete;
        }

        var empty = _empty.Get(error is null ? "No matching work found" : "Couldn't search GitHub",
            error ?? (query.Length == 0 ? "Use a preset or enter a GitHub search query." : "Try another qualifier or a narrower query."), refresh: true);
        var form = QueryForm();
        if (empty.MoreCommands.OfType<CommandContextItem>().FirstOrDefault()?.Command != form)
        {
            empty.MoreCommands = [new CommandContextItem(form)];
        }

        EmptyContent = empty;
        if (account is null) { return []; }
        if (query.Length == 0)
        {
            var presets = new List<IListItem>
            {
                Preset("Issues assigned to you", "is:open assignee:@me", IssueSearchKind.Issues, account, generation),
                Preset("Issues you authored", "author:@me", IssueSearchKind.Issues, account, generation),
                Preset("Pull requests awaiting your review", "is:open review-requested:@me", IssueSearchKind.PullRequests, account, generation),
                Preset("Pull requests you authored", "author:@me", IssueSearchKind.PullRequests, account, generation),
                new ListItem(form) { Title = "Run or save a search query", Subtitle = "Saved queries live on this device, separated by host and account" },
            };
            try
            {
                presets.AddRange(_store.Load(account).Select(saved => Preset(saved.Name, saved.Query, saved.Kind, account, generation)));
            }
            catch (Exception ex) when (ex is GitHubApiException or IOException or UnauthorizedAccessException)
            {
                _storeError = "Couldn't read your saved queries. Check local storage before saving another query.";
                presets.Add(new ListItem(form) { Title = "Saved queries unavailable", Subtitle = _storeError });
            }

            return [.. presets];
        }

        if (items.Length == 0 && error is not null)
        {
            return [new ListItem(new RefreshCommand(this))
            {
                Title = "Couldn't search GitHub",
                Subtitle = error,
                MoreCommands = [new CommandContextItem(form)],
            }];
        }

        IListItem[] rows = [.. items.Select(item => new ListItem(new SafeOpenCommand(this, account, generation, item.WebUrl))
        {
            Title = $"#{item.Number} {item.Title}",
            Subtitle = $"{item.Repository} · {(item.IsPullRequest ? "Pull request" : "Issue")} · {item.State}",
            Icon = item.IsPullRequest ? Icons.PullRequests : Icons.Issues,
            Tags = [new Tag(item.IsPullRequest ? "Pull request" : "Issue")],
            Details = new SearchDetails(item),
            MoreCommands = [new CommandContextItem(form), new CommandContextItem(new RefreshCommand(this)),
                new CommandContextItem(new CopyTextCommand(item.WebUrl.AbsoluteUri) { Name = "Copy URL" }),
                .. _nativeDetails.Commands($"{generation}:{item.Id}", account, item.Repository, item.Number,
                    item.IsPullRequest, () => Volatile.Read(ref _generation) == generation,
                    Icons.SubjectIcon(item.IsPullRequest, item.State == "open"
                        ? Notifications.SubjectState.Open : item.State == "closed"
                        ? Notifications.SubjectState.Closed : Notifications.SubjectState.Unknown))],
        })];
        return _pagination.Append(rows,
            total > IssueSearchClient.ResultLimit ? "GitHub search is limited to 1,000 results"
                : items.Length == 0 && !fetching ? "No matching work found" : "Search results",
            $"Showing {items.Length} of {total}. {(incomplete ? "GitHub returned incomplete results. Narrow your query or refresh." : "Search spans accessible repositories.")}",
            next is not null, fetching, error);
    }

    private ListItem Preset(string title, string query, IssueSearchKind kind, GitHubAccount account, int generation) =>
        new(new SearchCommand(this, account, generation, query, kind)) { Title = title, Subtitle = query };

    internal Task ExecuteQuery(string query, IssueSearchKind kind)
    {
        _ = IssueSearchClient.ScopeQuery(query, kind);
        lock (_load.SyncRoot)
        {
            if (_load.Disposed) { return Task.CompletedTask; }
            _load.Invalidate(reset: true);
            Interlocked.Increment(ref _generation);
            _query = query.Trim();
            _kind = kind;
            _items.Clear();
            _total = 0;
            _incomplete = false;
            _form = null;
        }

        _nativeDetails.Dispose();
        HasMoreItems = false;
        RaiseItemsChanged();
        return StartLoad(true);
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (oldSearch.Trim() == newSearch.Trim()) { return; }
        if (string.IsNullOrWhiteSpace(newSearch))
        {
            lock (_load.SyncRoot)
            {
                _load.Invalidate(reset: true);
                Interlocked.Increment(ref _generation);
                _query = "";
                _items.Clear();
                _form = null;
            }
            _nativeDetails.Dispose();
            HasMoreItems = false;
            RaiseItemsChanged();
        }
        else { _ = ExecuteQuery(newSearch, _kind); }
    }

    public override void LoadMore() => StartLoad(false);

    internal BrowsingFormPage QueryForm()
    {
        if (_form is not null) { return _form; }
        var account = _auth.CurrentAccount;
        var generation = _generation;
        return _form = new("Run or save a search query", string.Join(',',
            BrowsingFormPage.Text("query", "GitHub query", _query),
            BrowsingFormPage.Choice("kind", "Result type", ((int)_kind).ToString(System.Globalization.CultureInfo.InvariantCulture), ("Issues and pull requests", "0"), ("Issues", "1"), ("Pull requests", "2")),
            BrowsingFormPage.Text("name", "Saved query name (for save or delete)"),
            BrowsingFormPage.Choice("action", "Action", "run", ("Run query", "run"), ("Save query", "save"), ("Delete saved query", "delete"))),
            inputs =>
            {
                if (!IsCurrent(account, generation)) { return "Your account changed. Open the query form again."; }
                var query = GitHubRest.GetString(inputs, "query") ?? "";
                var name = GitHubRest.GetString(inputs, "name")?.Trim() ?? "";
                if (!int.TryParse(GitHubRest.GetString(inputs, "kind"), out var value) || !Enum.IsDefined((IssueSearchKind)value))
                {
                    return "Choose a result type.";
                }

                var kind = (IssueSearchKind)value;
                switch (GitHubRest.GetString(inputs, "action"))
                {
                    case "save": _store.Save(account!, new(name, query, kind)); break;
                    case "delete":
                        if (name.Length == 0) { return "Enter the saved query name to delete."; }
                        _store.Delete(account!, name);
                        break;
                    case "run": ExecuteQuery(query, kind); break;
                    default: return "Choose an action.";
                }

                _storeError = null;
                RaiseItemsChanged();
                return null;
            });
    }

    private Task StartLoad(bool reset)
    {
        ListLoadState.Operation operation;
        GitHubAccount account;
        string query;
        IssueSearchKind kind;
        lock (_load.SyncRoot)
        {
            if (_auth.CurrentAccount is not { } current || _query.Length == 0 || !_load.TryBegin(reset, out operation)) { return CurrentLoad; }
            account = current;
            query = _query;
            kind = _kind;
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), operation.Token).ConfigureAwait(false);
            var result = await _client.SearchAsync(account, query, kind, operation.Page, operation.Token).ConfigureAwait(false);
            lock (_load.SyncRoot)
            {
                if (!_load.IsCurrent(operation) || _auth.CurrentAccount != account) { return; }
                if (operation.Reset) { _items.Clear(); }
                var known = _items.Select(i => i.Id).ToHashSet();
                _items.AddRange(result.Items.Where(i => known.Add(i.Id)));
                _total = result.Total;
                _incomplete = result.Incomplete;
                _load.Succeed(operation, result.NextPage);
            }
        }, () =>
        {
            bool more;
            lock (_load.SyncRoot) { more = _load.NextPage is not null; }
            _load.Publish(operation, () => HasMoreItems = more);
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to search. Try a narrower query.", area: DiagnosticArea.Issues);
    }

    private void AccountChanged(object? sender, EventArgs e)
    {
        lock (_load.SyncRoot)
        {
            _load.Invalidate(reset: true);
            _items.Clear();
            _query = "";
            _kind = IssueSearchKind.All;
            Interlocked.Increment(ref _generation);
            _form = null;
            _total = 0;
            _storeError = null;
        }

        _nativeDetails.Dispose();
        SearchText = "";
        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    public void Dispose()
    {
        _auth.AccountChanged -= AccountChanged;
        lock (_load.SyncRoot) { Interlocked.Increment(ref _generation); _load.Dispose(); _items.Clear(); }
        _nativeDetails.Dispose();
    }

    private sealed partial class SearchDetails : Details
    {
        internal SearchDetails(IssueSearchResult result)
        {
            Title = result.Title;
            Body = result.Body ?? "No description returned.";
            Metadata = [
                new DetailsElement { Key = "Type", Data = new DetailsLink { Text = result.IsPullRequest ? "Pull request" : "Issue" } },
                new DetailsElement { Key = "Repository", Data = new DetailsLink { Text = result.Repository } },
                new DetailsElement { Key = "Author", Data = new DetailsLink { Text = result.Author ?? "not returned" } },
            ];
        }
    }

    private sealed partial class SearchCommand(IssueSearchPage page, GitHubAccount account, int generation, string query, IssueSearchKind kind) : InvokableCommand
    {
        public override string Name { get; set; } = "Run search";
        public override ICommandResult Invoke()
        {
            if (page.IsCurrent(account, generation)) { _ = page.ExecuteQuery(query, kind); }
            return CommandResult.KeepOpen();
        }
    }

    private sealed partial class SafeOpenCommand(IssueSearchPage page, GitHubAccount account, int generation, Uri url) : InvokableCommand
    {
        public override string Name { get; set; } = "Open on GitHub";
        public override ICommandResult Invoke()
        {
            if (!page.IsCurrent(account, generation)) { return CommandResult.KeepOpen(); }
            page._browser.Open(url);
            return CommandResult.Dismiss();
        }
    }

    private sealed partial class RefreshCommand(IssueSearchPage page) : InvokableCommand
    {
        public override string Name { get; set; } = "Refresh search";
        public override ICommandResult Invoke()
        {
            lock (page._load.SyncRoot) { page._load.Invalidate(); }
            _ = page.StartLoad(true);
            return CommandResult.KeepOpen();
        }
    }
}
