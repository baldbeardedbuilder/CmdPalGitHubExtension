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
    private readonly IAgentsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<AgentItem> _items = [];

    public AgentsPage(AuthService auth, IAgentsClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Agents, new RefreshAgentsCommand(this));
        Id = PageId;
        Name = "Open";
        Title = "Agents";
        Icon = Icons.Agents;
        PlaceholderText = "Filter agents...";
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

    public override IListItem[] GetItems()
    {
        bool needsLoad;
        lock (_lock)
        {
            needsLoad = _load.NeedsLoad;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        CommandItem empty;
        IListItem[] result;
        lock (_lock)
        {
            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            empty = _load.Fetching
                ? Empty("Loading agents...", "Checking your GitHub agent tasks")
                : _load.Error is not null
                    ? Empty("Couldn't load agents", _load.Error)
                    : terms.Length > 0
                        ? Empty("No agents found", $"Nothing matches \"{SearchText.Trim()}\"")
                        : Empty("No agents yet", "Your Copilot cloud agent tasks show up here");

            var items = _items.Where(i => i.Matches(terms)).Cast<IListItem>().ToList();
            if (_load.Error is not null && _items.Count > 0)
            {
                items.Insert(0, new ListItem(new RefreshAgentsCommand(this))
                {
                    Title = "Couldn't load agents",
                    Subtitle = _load.Error,
                    Icon = Icons.Agents,
                });
            }

            result = [.. items];
        }

        EmptyContent = empty;
        return result;
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
        _auth.AccountChanged -= OnAccountChanged;
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
            "GitHub took too long to respond. Try refreshing agents.");
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        var result = await _client.GetTasksAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
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
            _items.Clear();
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }

}
