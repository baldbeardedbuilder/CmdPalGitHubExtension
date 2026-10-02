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
    private readonly Lock _lock = new();
    private readonly List<CodespaceItem> _items = [];
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private bool _disposed;
    private string? _error;
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private Task _currentLoad = Task.CompletedTask;

    public CodespacesPage(AuthService auth, ICodespacesClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
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

    public override IListItem[] GetItems()
    {
        lock (_lock)
        {
            if (_auth.CurrentAccount is not { } account)
            {
                EmptyContent = Empty("Sign in to see your codespaces", "Your codespaces show up here after you sign in");
                return [];
            }

            if (!account.Host.IsGitHubDotCom)
            {
                EmptyContent = Empty("Codespaces isn't available here", "GitHub Enterprise Server doesn't support Codespaces. Sign in to github.com to see yours.");
                return [];
            }

            if (!_loaded && !_fetching)
            {
                StartLoad(reset: true);
            }

            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var items = _items.Where(i => i.Matches(terms)).Cast<IListItem>().ToList();
            var refresh = new RefreshCodespacesCommand(this);
            if (_error is not null)
            {
                var error = new ListItem(refresh) { Title = "Couldn't load codespaces", Subtitle = _error, Icon = Icons.Codespaces };
                EmptyContent = error;
                if (items.Count > 0)
                {
                    items.Add(error);
                }
            }
            else
            {
                EmptyContent = _fetching && items.Count == 0
                    ? Empty("Loading codespaces...", "Getting your development environments from GitHub")
                    : new CommandItem(refresh)
                    {
                        Title = terms.Length == 0 ? "No codespaces yet" : "No codespaces found",
                        Subtitle = terms.Length == 0 ? "Create a codespace on GitHub, then refresh" : $"Nothing matches \"{SearchText.Trim()}\"",
                        Icon = Icons.Codespaces,
                    };
            }

            return [.. items];
        }
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => RaiseItemsChanged();

    public override void LoadMore() => StartLoad(reset: false);

    public Task RefreshAsync()
    {
        lock (_lock)
        {
            CancelLoad();
            return StartLoad(reset: true);
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
    }

    private static CommandItem Empty(string title, string subtitle) =>
        new(new NoOpCommand()) { Title = title, Subtitle = subtitle, Icon = Icons.Codespaces };

    private Task StartLoad(bool reset)
    {
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount is not { } account || !account.Host.IsGitHubDotCom
                || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            var page = reset ? null : _nextPage;
            var generation = _generation;
            _fetching = true;
            _error = null;
            IsLoading = true;
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
                HasMoreItems = _nextPage is not null;
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
                _loaded = true;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation)
                {
                    _fetching = false;
                    IsLoading = false;
                    RaiseItemsChanged();
                }
            }
        }
    }

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _fetching = false;
        IsLoading = false;
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
            HasMoreItems = false;
        }

        RaiseItemsChanged();
    }
}
