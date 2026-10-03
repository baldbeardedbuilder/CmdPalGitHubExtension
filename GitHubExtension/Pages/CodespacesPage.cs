// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class CodespacesPage : DynamicListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.codespaces";

    private readonly AuthService _auth;
    private readonly ICodespacesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly IContextItem[] _createCommands;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<CodespaceItem> _items = [];
    private string _errorTitle = "Couldn't load codespaces";

    public CodespacesPage(
        AuthService auth,
        ICodespacesClient client,
        IBrowserLauncher browser,
        TimeProvider? time = null,
        CreateCodespacePage? createPage = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
        _emptyContent = new PageEmptyContent(Icons.Codespaces, new RefreshCodespacesCommand(this));
        CreatePage = createPage;
        _createCommands = createPage is null ? [] : [new CommandContextItem(createPage)];
        Id = PageId;
        Name = "Open";
        Title = "Codespaces";
        Icon = Icons.Codespaces;
        PlaceholderText = "Filter codespaces...";
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

    internal CreateCodespacePage? CreatePage { get; }

    public override IListItem[] GetItems()
    {
        var account = _auth.CurrentAccount;
        if (account is null || !account.Host.IsGitHubDotCom)
        {
            EmptyContent = account is null
                ? Empty("Sign in to see your codespaces", "Your codespaces show up here after you sign in")
                : Empty("Codespaces isn't available here", "GitHub Enterprise Server doesn't support Codespaces. Sign in to github.com to see yours.");
            return [];
        }

        bool needsLoad;
        lock (_lock)
        {
            needsLoad = _load.NeedsLoad;
        }

        if (needsLoad)
        {
            StartLoad(reset: true);
        }

        ICommandItem empty;
        IListItem[] result;
        lock (_lock)
        {
            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var items = _items.Where(i => i.Matches(terms)).Cast<IListItem>().ToList();
            var refresh = new RefreshCodespacesCommand(this);
            if (_load.Error is not null)
            {
                var error = new ListItem(refresh)
                {
                    Title = _errorTitle, Subtitle = _load.Error, Icon = Icons.Codespaces,
                    MoreCommands = _createCommands,
                };
                empty = Empty(_errorTitle, _load.Error, refresh: true);
                if (items.Count > 0)
                {
                    items.Add(error);
                }
            }
            else
            {
                empty = _load.Fetching && items.Count == 0
                    ? Empty("Loading codespaces...", "Getting your development environments from GitHub")
                    : Empty(terms.Length == 0 ? "No codespaces yet" : "No codespaces found",
                        terms.Length == 0
                            ? CreatePage is null ? "Create a codespace on GitHub, then refresh" : "Use More > Create Codespace to create one, then refresh"
                            : $"Nothing matches \"{SearchText.Trim()}\"",
                        refresh: true);
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

    internal Task CloseAsync(CodespaceItem item)
        => RunCodespaceActionAsync(
            item,
            "Available",
            "Couldn't close codespace",
            (account, name, token) => _client.StopCodespaceAsync(account, name, token));

    internal Task StartAsync(CodespaceItem item)
        => RunCodespaceActionAsync(
            item,
            "Shutdown",
            "Couldn't start codespace",
            (account, name, token) => _client.StartCodespaceAsync(account, name, token),
            refreshUntilAvailable: true);

    private Task RunCodespaceActionAsync(
        CodespaceItem item,
        string requiredState,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        bool refreshUntilAvailable = false)
    {
        GitHubAccount account;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (!_items.Contains(item) || item.Codespace.State != requiredState
                || _auth.CurrentAccount is not { Host.IsGitHubDotCom: true } currentAccount
                || !_load.TryBegin(true, out operation))
            {
                return _load.CurrentLoad;
            }

            account = currentAccount;
            _errorTitle = errorTitle;
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => RunCodespaceActionCoreAsync(account, item, operation, action, refreshUntilAvailable),
            () => PublishLoad(operation), "GitHub took too long to respond. Try refreshing codespaces.", markLoadedOnError: false);
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

    private CommandItem Empty(string title, string subtitle, bool refresh = false)
    {
        var empty = _emptyContent.Get(title, subtitle, refresh);
        if (!ReferenceEquals(empty.MoreCommands, _createCommands))
        {
            empty.MoreCommands = _createCommands;
        }

        return empty;
    }

    private Task StartLoad(bool reset)
    {
        GitHubAccount account;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_auth.CurrentAccount is not { } currentAccount || !currentAccount.Host.IsGitHubDotCom
                || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }

            account = currentAccount;
            _errorTitle = "Couldn't load codespaces";
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(account, operation), () => PublishLoad(operation),
            "GitHub took too long to respond. Try refreshing codespaces.");
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        var result = await _client.GetCodespacesAsync(account, operation.Page, operation.Token).ConfigureAwait(false);
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

            var known = _items.Select(i => i.Codespace.Name).ToHashSet(StringComparer.Ordinal);
            _items.AddRange(result.Codespaces.Where(c => known.Add(c.Name)).Select(c => new CodespaceItem(this, c, _browser, now)));
            _items.Sort((a, b) => b.Codespace.LastUsedAt.CompareTo(a.Codespace.LastUsedAt));
            _load.Succeed(operation, result.NextPage);
        }
    }

    private async Task RunCodespaceActionCoreAsync(
        GitHubAccount account,
        CodespaceItem item,
        ListLoadState.Operation operation,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        bool refreshUntilAvailable)
    {
        var token = operation.Token;
        var name = item.Codespace.Name;
        var codespace = await action(account, name, token).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (!_load.IsCurrent(operation))
                {
                    return;
                }

                var index = _items.IndexOf(item);
                if (index >= 0)
                {
                    item = new CodespaceItem(this, codespace, _browser, _time.GetUtcNow());
                    _items[index] = item;
                }
            }

            if (!refreshUntilAvailable || codespace.State == "Available")
            {
                break;
            }

            _load.Publish(operation, () => RaiseItemsChanged());
            if (codespace.State is not ("Shutdown" or "Created" or "Queued" or "Provisioning" or "Starting" or "Updating" or "Awaiting" or "Rebuilding"))
            {
                throw new GitHubApiException("This codespace couldn't become available. Refresh to check its state.");
            }

            if (attempt >= 60)
            {
                throw new GitHubApiException("This codespace is still starting. Refresh to check its state.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), _time, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            codespace = await _client.GetCodespaceAsync(account, name, token).ConfigureAwait(false);
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
