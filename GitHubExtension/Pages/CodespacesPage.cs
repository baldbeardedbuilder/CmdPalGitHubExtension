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
    private readonly Lock _lock = new();
    private readonly List<CodespaceItem> _items = [];
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private bool _disposed;
    private string? _error;
    private string _errorTitle = "Couldn't load codespaces";
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private Task _currentLoad = Task.CompletedTask;

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
                return _currentLoad;
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
            needsLoad = !_loaded && !_fetching;
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
            if (_error is not null)
            {
                var error = new ListItem(refresh)
                {
                    Title = _errorTitle, Subtitle = _error, Icon = Icons.Codespaces,
                    MoreCommands = _createCommands,
                };
                empty = Empty(_errorTitle, _error, refresh: true);
                if (items.Count > 0)
                {
                    items.Add(error);
                }
            }
            else
            {
                empty = _fetching && items.Count == 0
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
            CancelLoad();
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
            (account, name, token) => _client.StartCodespaceAsync(account, name, token));

    private Task RunCodespaceActionAsync(
        CodespaceItem item,
        string requiredState,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action)
    {
        GitHubAccount account;
        CancellationToken token;
        int generation;
        lock (_lock)
        {
            if (_disposed || _fetching || !_items.Contains(item) || item.Codespace.State != requiredState
                || _auth.CurrentAccount is not { Host.IsGitHubDotCom: true } currentAccount)
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            token = _loadCts.Token;
            account = currentAccount;
            generation = _generation;
            _fetching = true;
            _error = null;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation || _disposed)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => RunCodespaceActionCoreAsync(account, item, generation, errorTitle, action, token));
            return _currentLoad;
        }
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            _disposed = true;
            CancelLoad();
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
        CancellationToken token;
        Uri? page;
        int generation;
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount is not { } currentAccount || !currentAccount.Host.IsGitHubDotCom
                || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            account = currentAccount;
            token = _loadCts.Token;
            page = reset ? null : _nextPage;
            generation = _generation;
            _fetching = true;
            _error = null;
            _errorTitle = "Couldn't load codespaces";
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation || _disposed)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => LoadAsync(account, page, reset, generation, token));
            return _currentLoad;
        }
    }

    private async Task LoadAsync(GitHubAccount account, Uri? page, bool reset, int generation, CancellationToken token)
    {
        try
        {
            var result = await _client.GetCodespacesAsync(account, page, token).ConfigureAwait(false);
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

                var known = _items.Select(i => i.Codespace.Name).ToHashSet(StringComparer.Ordinal);
                _items.AddRange(result.Codespaces.Where(c => known.Add(c.Name)).Select(c => new CodespaceItem(this, c, _browser, now)));
                _items.Sort((a, b) => b.Codespace.LastUsedAt.CompareTo(a.Codespace.LastUsedAt));
                _nextPage = result.NextPage;
                _loaded = true;
                hasMore = _nextPage is not null;
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
            CompleteOperation(generation);
        }
    }

    private async Task RunCodespaceActionCoreAsync(
        GitHubAccount account,
        CodespaceItem item,
        int generation,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        CancellationToken token)
    {
        try
        {
            var codespace = await action(account, item.Codespace.Name, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                var index = _items.IndexOf(item);
                if (index >= 0)
                {
                    _items[index] = new CodespaceItem(this, codespace, _browser, _time.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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
                _errorTitle = errorTitle;
            }
        }
        finally
        {
            CompleteOperation(generation);
        }
    }

    private void CompleteOperation(int generation)
    {
        bool publish;
        lock (_lock)
        {
            publish = generation == _generation;
            if (publish)
            {
                _fetching = false;
            }
        }

        if (publish)
        {
            IsLoading = false;
            RaiseItemsChanged();
        }
    }

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _fetching = false;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            CancelLoad();
            _items.Clear();
            _nextPage = null;
            _loaded = false;
            _error = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }
}
