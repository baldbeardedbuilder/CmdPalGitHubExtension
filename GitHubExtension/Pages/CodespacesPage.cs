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
    private readonly MutationExecutor _mutations;
    private readonly TimeProvider _time;
    private readonly PageEmptyContent _emptyContent;
    private readonly IContextItem[] _createCommands;
    private readonly Lock _lock = new();
    private readonly List<CodespaceItem> _items = [];
    private Uri? _nextPage;
    private GitHubAccount? _loadedAccount;
    private bool _loaded;
    private bool _fetching;
    private bool _disposed;
    private string? _error;
    private Uri? _authorizeUrl;
    private Uri? _authorizeCommandUrl;
    private ICommand? _authorizeCommand;
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
        _mutations = new MutationExecutor(auth);
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
            if (_authorizeUrl != _authorizeCommandUrl)
            {
                _authorizeCommandUrl = _authorizeUrl;
                _authorizeCommand = _authorizeUrl is { } authorize
                    ? new OpenInBrowserCommand(_browser, authorize, "Authorize organization access", Icons.Codespaces)
                    : null;
            }

            ICommand refresh = _authorizeCommand ?? new RefreshCodespacesCommand(this);
            if (_error is not null)
            {
                var error = new ListItem(refresh)
                {
                    Title = _errorTitle, Subtitle = _error, Icon = Icons.Codespaces,
                    MoreCommands = _createCommands,
                };
                empty = Empty(_errorTitle, _error, refresh: true, command: _authorizeCommand);
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
            (account, name, token) => _client.StartCodespaceAsync(account, name, token),
            refreshUntilAvailable: true);

    internal ICommand StartConfirmation(CodespaceItem item)
    {
        var account = _auth.CurrentAccount;
        return account is null
            ? new RefreshCodespacesCommand(this)
            : new MutationConfirmationPage(account, "Start Codespace", $"{item.Codespace.RepositoryFullName} / {item.Codespace.Name}",
                "Starting this Codespace uses compute and may incur charges.",
                () => _mutations.IsCurrent(account) ? StartAsync(item) : Task.CompletedTask,
                () =>
                {
                    lock (_lock)
                    {
                        return !_mutations.IsCurrent(account)
                            ? ("The account changed. Return to Codespaces and review the action again.", null)
                            : (_error ?? "Request completed. Return to Codespaces and refresh to check its state.", _authorizeUrl);
                    }
                },
                () => _mutations.IsCurrent(account));
    }

    private Task RunCodespaceActionAsync(
        CodespaceItem item,
        string requiredState,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        bool refreshUntilAvailable = false)
    {
        GitHubAccount account;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed || _fetching || !_items.Contains(item) || item.Codespace.State != requiredState
                || _auth.CurrentAccount is not { Host.IsGitHubDotCom: true } currentAccount
                || !ReferenceEquals(currentAccount, _loadedAccount))
            {
                return _currentLoad;
            }

            account = currentAccount;
            generation = _generation;
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            token = _loadCts.Token;
            _fetching = true;
            _error = null;
            _authorizeUrl = null;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (generation != _generation || _disposed)
            {
                return _currentLoad;
            }

            _currentLoad = Task.Run(() => RunCodespaceActionCoreAsync(account, item, generation, requiredState, errorTitle, action, refreshUntilAvailable, token));
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

        _mutations.Dispose();
        IsLoading = false;
    }

    private CommandItem Empty(string title, string subtitle, bool refresh = false, ICommand? command = null)
    {
        var empty = _emptyContent.Get(title, subtitle, refresh, command);
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
            foreach (var codespace in result.Codespaces)
            {
                if (codespace.State is "Available" or "Shutdown")
                {
                    _mutations.ObserveCompletion(account,
                        $"codespace:{codespace.Name}:{(codespace.State == "Available" ? "Shutdown" : "Available")}");
                }
            }
            var now = _time.GetUtcNow();
            bool hasMore;
            lock (_lock)
            {
                if (generation != _generation || !ReferenceEquals(account, _auth.CurrentAccount))
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
                _loadedAccount = account;
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
                _authorizeUrl = ex.AuthorizeUrl;
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
        string requiredState,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        bool refreshUntilAvailable,
        CancellationToken pollingToken)
    {
        try
        {
            var desiredState = requiredState == "Shutdown" ? "Available" : "Shutdown";
            async Task<GitHubCodespace?> Read(CancellationToken token)
            {
                Uri? page = null;
                do
                {
                    var response = await _client.GetCodespacesAsync(account, page, token).ConfigureAwait(false);
                    var found = response.Codespaces.FirstOrDefault(c => c.Name == item.Codespace.Name);
                    if (found is not null)
                    {
                        return found;
                    }

                    page = response.NextPage;
                }
                while (page is not null);
                return null;
            }

            var result = await _mutations.ExecuteAsync(
                account, $"codespace:{item.Codespace.Name}:{requiredState}",
                async token =>
                {
                    var fresh = await Read(token).ConfigureAwait(false);
                    return fresh is not null && fresh.State == requiredState
                        && fresh.RepositoryFullName == item.Codespace.RepositoryFullName
                        && fresh.Branch == item.Codespace.Branch;
                },
                async token =>
                {
                    var updated = await action(account, item.Codespace.Name, token).ConfigureAwait(false);
                    if (updated.Name != item.Codespace.Name
                        || updated.RepositoryFullName != item.Codespace.RepositoryFullName
                        || updated.Branch != item.Codespace.Branch)
                    {
                        return new MutationResult<GitHubCodespace>(MutationState.Unknown, Error: "GitHub returned a different Codespace. Refresh to check its state.");
                    }

                    try
                    {
                        var authoritative = await Read(token).ConfigureAwait(false);
                        if (authoritative is null
                            || authoritative.RepositoryFullName != item.Codespace.RepositoryFullName
                            || authoritative.Branch != item.Codespace.Branch)
                        {
                            return new MutationResult<GitHubCodespace>(MutationState.Unknown,
                                Error: "Couldn't verify this Codespace after the request. Refresh to check GitHub before retrying.");
                        }

                        return refreshUntilAvailable
                            ? await PollUntilAvailableAsync(authoritative, token).ConfigureAwait(false)
                            : new MutationResult<GitHubCodespace>(
                                authoritative.State == desiredState ? MutationState.Completed : MutationState.Pending, authoritative);
                    }
                    catch (GitHubApiException ex)
                    {
                        return new MutationResult<GitHubCodespace>(MutationState.Unknown,
                            Error: ex.Message, AuthorizeUrl: ex.AuthorizeUrl);
                    }
                },
                async token =>
                {
                    var fresh = await Read(token).ConfigureAwait(false);
                    if (fresh is not null && (fresh.RepositoryFullName != item.Codespace.RepositoryFullName
                        || fresh.Branch != item.Codespace.Branch))
                    {
                        return new MutationResult<GitHubCodespace>(MutationState.Unknown,
                            Error: "This Codespace's repository or branch changed. Check GitHub before retrying.");
                    }

                    return fresh?.State == desiredState
                        ? new MutationResult<GitHubCodespace>(MutationState.Completed, fresh)
                        : new MutationResult<GitHubCodespace>(MutationState.Pending, fresh,
                            "GitHub may still be processing this request. Refresh to check its state; no duplicate request was sent.");
                }, cancellationToken: pollingToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation || !_mutations.IsCurrent(account) || result.State == MutationState.Stale)
                {
                    return;
                }

                var index = _items.IndexOf(item);
                if (index >= 0 && result.Value is { } codespace)
                {
                    _items[index] = new CodespaceItem(this, codespace, _browser, _time.GetUtcNow());
                }

                if (result.State != MutationState.Completed)
                {
                    _error = result.Error ?? "GitHub is processing this request. Refresh to check its state before retrying.";
                    _errorTitle = errorTitle;
                    _authorizeUrl = result.AuthorizeUrl;
                }
            }

            async Task<MutationResult<GitHubCodespace>> PollUntilAvailableAsync(GitHubCodespace codespace, CancellationToken sessionToken)
            {
                using var polling = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, pollingToken);
                var token = polling.Token;
                for (var attempt = 0; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (codespace.Name != item.Codespace.Name
                        || codespace.RepositoryFullName != item.Codespace.RepositoryFullName
                        || codespace.Branch != item.Codespace.Branch)
                    {
                        return new(MutationState.Unknown, Error: "GitHub returned a different Codespace. Refresh to check its state.");
                    }

                    if (codespace.State == "Available")
                    {
                        return new(MutationState.Completed, codespace);
                    }

                    lock (_lock)
                    {
                        if (generation != _generation || !_mutations.IsCurrent(account))
                        {
                            return new(MutationState.Pending);
                        }

                        var index = _items.IndexOf(item);
                        if (index >= 0)
                        {
                            item = new CodespaceItem(this, codespace, _browser, _time.GetUtcNow());
                            _items[index] = item;
                        }
                    }

                    RaiseItemsChanged();
                    if (codespace.State is not ("Shutdown" or "Created" or "Queued" or "Provisioning" or "Starting" or "Updating" or "Awaiting" or "Rebuilding"))
                    {
                        return new(MutationState.Pending, codespace, "This codespace couldn't become available. Refresh to check its state.");
                    }

                    if (attempt >= 60)
                    {
                        return new(MutationState.Pending, codespace, "This codespace is still starting. Refresh to check its state.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), _time, token).ConfigureAwait(false);
                    codespace = await _client.GetCodespaceAsync(account, codespace.Name, token).WaitAsync(token).ConfigureAwait(false);
                }
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
            publish = generation == _generation && !_disposed;
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
            _loadedAccount = null;
            _error = null;
            _authorizeUrl = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }
}
