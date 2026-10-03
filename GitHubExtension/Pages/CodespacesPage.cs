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
    private readonly HashSet<string> _pendingDeletes = new(StringComparer.Ordinal);
    private bool _reconcileDeletes;
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

    internal (GitHubAccount? Account, int Generation, CancellationToken Token) DeleteContext()
    {
        lock (_lock)
        {
            return (_auth.CurrentAccount, _generation, _loadCts?.Token ?? new CancellationToken(true));
        }
    }

    internal bool CanDelete(CodespaceItem item, GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            return !_disposed && generation == _generation && account is { Host.IsGitHubDotCom: true }
                && ReferenceEquals(account, _auth.CurrentAccount) && _items.Contains(item)
                && !_pendingDeletes.Contains(item.Codespace.Name);
        }
    }

    internal bool IsDeleteContextCurrent(GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            return !_disposed && generation == _generation && account is { Host.IsGitHubDotCom: true }
                && ReferenceEquals(account, _auth.CurrentAccount);
        }
    }

    internal Task<GitHubCodespace> GetDeleteDetailsAsync(GitHubAccount account, string name, CancellationToken token)
        => _client.GetCodespaceAsync(account, name, token);

    internal async Task<string> DeleteAsync(CodespaceItem item, GitHubAccount account, int generation)
    {
        CancellationToken token;
        lock (_lock)
        {
            if (_fetching || !CanDelete(item, account, generation))
            {
                return "This codespace is no longer current. Refresh the list.";
            }

            token = _loadCts!.Token;
            _fetching = true;
            _error = null;
            _reconcileDeletes = true;
        }

        IsLoading = true;
        try
        {
            token.ThrowIfCancellationRequested();
            await _client.DeleteCodespaceAsync(account, item.Codespace.Name, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation || _disposed)
                {
                    return "The account or list changed.";
                }

                _pendingDeletes.Add(item.Codespace.Name);
            }

            var codespaces = await GetAllCodespacesAsync(account, token).ConfigureAwait(false);
            bool present;
            lock (_lock)
            {
                if (generation != _generation || _disposed)
                {
                    return "The account or list changed.";
                }

                present = codespaces.Any(c => c.Name == item.Codespace.Name);
                ApplyReconciliation(codespaces);
                if (present)
                {
                    _errorTitle = "Deletion pending";
                    _error = "GitHub accepted the deletion, but the codespace is still present. Refresh to check again.";
                }
            }

            HasMoreItems = false;
            return present ? "Deletion pending. Refresh to check again." : "Codespace deleted.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return "The account or list changed.";
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (generation == _generation && !_disposed)
                {
                    _errorTitle = "Couldn't confirm deletion";
                    _error = $"{ex.Message} Refresh to check whether the codespace still exists.";
                }
            }

            return $"{ex.Message} Refresh to check whether the codespace still exists.";
        }
        finally
        {
            CompleteOperation(generation);
        }
    }

    private async Task<List<GitHubCodespace>> GetAllCodespacesAsync(GitHubAccount account, CancellationToken token)
    {
        var result = new List<GitHubCodespace>();
        var visited = new HashSet<Uri>();
        int? totalCount = null;
        Uri? page = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var batch = await _client.GetCodespacesAsync(account, page, token).ConfigureAwait(false);
            if (!batch.IsComplete || (batch.NextPage is { } next
                && (!next.IsAbsoluteUri || !visited.Add(next) || next.Scheme != account.Host.ApiUrl.Scheme
                    || next.Authority != account.Host.ApiUrl.Authority
                    || next.AbsolutePath != "/user/codespaces")))
            {
                throw new GitHubApiException("GitHub's codespaces list was incomplete. No items were removed.");
            }

            if (batch.TotalCount is { } count)
            {
                if (totalCount is { } previous && previous != count)
                {
                    throw new GitHubApiException("GitHub's codespaces list changed while checking deletion. Refresh to check again.");
                }

                totalCount = count;
            }

            result.AddRange(batch.Codespaces);
            page = batch.NextPage;
        }
        while (page is not null);
        var unique = result.DistinctBy(c => c.Name, StringComparer.Ordinal).ToList();
        if (totalCount is { } expected && unique.Count != expected)
        {
            throw new GitHubApiException("GitHub's codespaces list was incomplete. No items were removed.");
        }

        return unique;
    }

    private void ApplyReconciliation(List<GitHubCodespace> codespaces)
    {
        _pendingDeletes.RemoveWhere(name => !codespaces.Any(c => c.Name == name));
        _reconcileDeletes = _pendingDeletes.Count > 0;
        _items.Clear();
        _items.AddRange(codespaces.OrderByDescending(c => c.LastUsedAt)
            .Select(c => new CodespaceItem(this, c, _browser, _time.GetUtcNow())));
        _nextPage = null;
        _loaded = true;
    }

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
            DiagnosticEvent.CodespaceStop,
            "Available",
            "Couldn't close codespace",
            (account, name, token) => _client.StopCodespaceAsync(account, name, token));

    internal Task StartAsync(CodespaceItem item)
        => RunCodespaceActionAsync(
            item,
            DiagnosticEvent.CodespaceStart,
            "Shutdown",
            "Couldn't start codespace",
            (account, name, token) => _client.StartCodespaceAsync(account, name, token),
            refreshUntilAvailable: true);

    private Task RunCodespaceActionAsync(
        CodespaceItem item,
        DiagnosticEvent diagnosticEvent,
        string requiredState,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        bool refreshUntilAvailable = false)
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

            _currentLoad = Task.Run(() => RunCodespaceActionCoreAsync(account, item, generation, diagnosticEvent, errorTitle, action, refreshUntilAvailable, token));
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
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.PageLoad, DiagnosticArea.Codespaces, verbose: true);
        Exception? failure = null;
        try
        {
            bool reconcile;
            lock (_lock)
            {
                reconcile = reset && _reconcileDeletes;
            }

            if (reconcile)
            {
                var codespaces = await GetAllCodespacesAsync(account, token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (generation != _generation || _disposed)
                    {
                        return;
                    }

                    ApplyReconciliation(codespaces);
                    if (_pendingDeletes.Count > 0)
                    {
                        _errorTitle = "Deletion pending";
                        _error = "The codespace is still present. Refresh to check again.";
                    }
                }

                HasMoreItems = false;
                return;
            }

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
        catch (Exception ex)
        {
            failure = ex;
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
            var current = CompleteOperation(generation);
            PageDiagnostics.Finish(operation, failure, current, cancellationToken: token);
        }
    }

    private async Task RunCodespaceActionCoreAsync(
        GitHubAccount account,
        CodespaceItem item,
        int generation,
        DiagnosticEvent diagnosticEvent,
        string errorTitle,
        Func<GitHubAccount, string, CancellationToken, Task<GitHubCodespace>> action,
        bool refreshUntilAvailable,
        CancellationToken token)
    {
        using var operation = OperationDiagnostics.Begin(diagnosticEvent, DiagnosticArea.Codespaces);
        Exception? failure = null;
        try
        {
            var name = item.Codespace.Name;
            var codespace = await action(account, name, token).ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                lock (_lock)
                {
                    if (generation != _generation)
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

                RaiseItemsChanged();
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
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
            var current = CompleteOperation(generation);
            if (current && !token.IsCancellationRequested && operation.ChildOutcome == DiagnosticOutcome.Failed)
            {
                operation.Complete();
            }
            else if (current && !token.IsCancellationRequested && failure is null && refreshUntilAvailable)
            {
                operation.Complete(DiagnosticOutcome.Completed);
            }
            else
            {
                PageDiagnostics.Finish(operation, failure, current, DiagnosticOutcome.Accepted, mutation: true, cancellationToken: token);
            }
        }
    }

    private bool CompleteOperation(int generation)
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

        lock (_lock)
        {
            return generation == _generation && !_disposed;
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
            _pendingDeletes.Clear();
            _reconcileDeletes = false;
            _nextPage = null;
            _loaded = false;
            _error = null;
        }

        HasMoreItems = false;
        IsLoading = false;
        RaiseItemsChanged();
    }
}
