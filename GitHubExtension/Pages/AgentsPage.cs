// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class AgentsPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.agents";

    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IAgentsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<AgentItem> _items = [];
    private AgentQuery _query = new();
    private int _generation;
    internal GitHubAccount? CurrentAccount => _auth.CurrentAccount;
    internal int Generation => _generation;
    internal bool Archived => _query.Archived;
    internal bool CanNavigate(GitHubAccount? account, int generation) =>
        !_load.Disposed && account is not null && _auth.CurrentAccount == account && generation == _generation;

    public AgentsPage(AuthService auth, IAgentsClient client, IBrowserLauncher browser, TimeProvider? time = null, AgentQuery? query = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _query = query ?? new();
        _emptyContent = new PageEmptyContent(Icons.Agents, new RefreshAgentsCommand(this));
        Id = PageId;
        Name = "Open";
        Title = _query.Repository is { } repository ? $"Agents in {repository}" : "Agents";
        Icon = Icons.Agents;
        PlaceholderText = "Filter agents...";
        _accountSubscription = _auth.Subscribe(this, static page => page.OnAccountChanged(null, EventArgs.Empty));
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

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        bool fetching;
        string? error;
        AgentItem[] snapshot;
        lock (_lock)
        {
            if (_load.Disposed) { return []; }
            needsLoad = _load.NeedsLoad;
            fetching = _load.Fetching;
            error = _load.Error;
            snapshot = [.. _items];
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var empty = fetching
            ? Empty("Loading agents...", "Checking your GitHub agent tasks")
            : error is not null
                ? Empty("Couldn't load agents", error)
                : terms.Length > 0
                    ? Empty("No agents found", $"Nothing matches \"{SearchText.Trim()}\"")
                    : Empty("No agents yet", "Your Copilot cloud agent tasks show up here");

        var items = snapshot.Where(i => i.Matches(terms)).Cast<IListItem>().ToList();
        if (error is not null && snapshot.Length > 0)
        {
            items.Insert(0, new ListItem(new RefreshAgentsCommand(this))
            {
                Title = "Couldn't load agents",
                Subtitle = error,
                Icon = Icons.Agents,
            });
        }

        EmptyContent = empty;
        empty.MoreCommands = QueryCommands();
        return [.. items];
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override void LoadMore() => StartLoad(reset: false);

    public Task RefreshAsync()
    {
        lock (_lock)
        {
            _load.Invalidate();
        }

        return StartLoad(reset: true);
    }

    public void Dispose()
    {
        _accountSubscription.Dispose();
        lock (_lock)
        {
            _load.Dispose();
        }

        IsLoading = false;
    }

    private CommandItem Empty(string title, string subtitle) =>
        _emptyContent.Get(title, subtitle, refresh: true);

    private Task StartLoad(bool reset)
    {
        GitHubAccount account;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_auth.CurrentAccount is not { } currentAccount || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }

            account = currentAccount;
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(account, operation), () => PublishLoad(operation),
            "GitHub took too long to respond. Try refreshing agents.", area: DiagnosticArea.Agents);
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        AgentQuery query;
        lock (_lock) { query = _query; }
        var result = _client is IAgentBrowsingClient browsing
            ? await browsing.GetTasksAsync(account, query, operation.Page, operation.Token).ConfigureAwait(false)
            : await _client.GetTasksAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || _auth.CurrentAccount != account)
            {
                return;
            }

            if (operation.Reset)
            {
                _items.Clear();
            }

            var known = _items.Select(i => i.Task.Id).ToHashSet(StringComparer.Ordinal);
            _items.AddRange(result.Tasks.Where(t => known.Add(t.Id))
                .Select(t => new AgentItem(this, t, _browser, _time.GetUtcNow())));
            _items.Sort((a, b) => b.Task.UpdatedAt.CompareTo(a.Task.UpdatedAt));
            _load.Succeed(operation, result.NextPage);
        }
    }

    private void PublishLoad(ListLoadState.Operation operation)
    {
        bool hasMore;
        lock (_lock)
        {
            hasMore = _load.NextPage is not null;
        }

        _load.Publish(operation, () => HasMoreItems = hasMore);
        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _load.Invalidate(reset: true);
            _generation++;
            _items.Clear();
            _query = new();
            _queryCommands = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

    private IContextItem[]? _queryCommands;
    internal IContextItem[] QueryCommands() => _queryCommands ??= _client is IAgentBrowsingClient ? [new CommandContextItem(QueryForm())] : [];

    internal BrowsingFormPage QueryForm()
    {
        var account = CurrentAccount;
        var generation = Generation;
        var query = _query;
        return new("Filter agent API results",
            string.Join(',', BrowsingFormPage.Choice("archived", "Task history", query.Archived ? "true" : "false",
                ("Non-archived tasks", "false"), ("Archived tasks", "true")),
                BrowsingFormPage.Choice("state", "State", query.State ?? "",
                    [("All states", ""), .. AgentQuery.States.Select(s => (AgentFormatting.StateText(s), s))]),
                BrowsingFormPage.Text("repository", "Repository (optional owner/name)", query.Repository ?? "")),
            inputs =>
            {
                if (!CanNavigate(account, generation)) { return "Your account or query changed. Open the filter again."; }
                var next = new AgentQuery(GitHubRest.GetString(inputs, "archived") == "true",
                    GitHubRest.GetString(inputs, "state"), GitHubJson.Optional(GitHubRest.GetString(inputs, "repository")));
                if (account is not null) { _ = AgentsClient.QueryUri(account, next); }
                SetQuery(next);
                return null;
            });
    }

    internal Task SetQuery(AgentQuery query)
    {
        lock (_lock)
        {
            if (_load.Disposed) { return Task.CompletedTask; }
            _load.Invalidate(reset: true);
            _query = query;
            _generation++;
            _queryCommands = null;
            _items.Clear();
        }

        HasMoreItems = false;
        RaiseItemsChanged();
        return StartLoad(true);
    }

    internal AgentDetailsPage DetailsPage(GitHubAgentTask task)
    {
        var account = CurrentAccount;
        var generation = Generation;
        return new(_client as IAgentBrowsingClient, task, _browser, account!, () => CanNavigate(account, generation), _auth);
    }

}
