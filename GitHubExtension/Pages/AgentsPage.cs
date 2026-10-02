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
    private readonly Lock _lock = new();
    private readonly List<AgentItem> _items = [];
    private CancellationTokenSource? _loadCts;
    private Uri? _nextPage;
    private bool _loaded;
    private bool _fetching;
    private bool _disposed;
    private string? _error;
    private int _generation;
    private Task _currentLoad = Task.CompletedTask;

    public AgentsPage(AuthService auth, IAgentsClient client, IBrowserLauncher browser, TimeProvider? time = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _time = time ?? TimeProvider.System;
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
                return _currentLoad;
            }
        }
    }

    public override IListItem[] GetItems()
    {
        lock (_lock)
        {
            if (!_loaded && !_fetching)
            {
                StartLoad(reset: true);
            }

            var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            EmptyContent = _fetching
                ? Empty("Loading agents...", "Checking your GitHub agent tasks")
                : _error is not null
                    ? Empty("Couldn't load agents", _error)
                    : terms.Length > 0
                        ? Empty("No agents found", $"Nothing matches \"{SearchText.Trim()}\"")
                        : Empty("No agents yet", "Your Copilot cloud agent tasks show up here");

            var items = _items.Where(i => i.Matches(terms)).Cast<IListItem>().ToList();
            if (_error is not null && _items.Count > 0)
            {
                items.Insert(0, new ListItem(new RefreshAgentsCommand(this))
                {
                    Title = "Couldn't load agents",
                    Subtitle = _error,
                    Icon = Icons.Agents,
                });
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

    private CommandItem Empty(string title, string subtitle) =>
        new(new RefreshAgentsCommand(this)) { Title = title, Subtitle = subtitle, Icon = Icons.Agents };

    private Task StartLoad(bool reset)
    {
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount is not { } account || _fetching || (!reset && _nextPage is null))
            {
                return _currentLoad;
            }

            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;
            var generation = _generation;
            var page = reset ? null : _nextPage;
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
            var result = await _client.GetTasksAsync(account, page, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested)
                {
                    return;
                }

                if (reset)
                {
                    _items.Clear();
                }

                var known = _items.Select(i => i.Task.Id).ToHashSet(StringComparer.Ordinal);
                _items.AddRange(result.Tasks.Where(t => known.Add(t.Id))
                    .Select(t => new AgentItem(this, t, _browser, _time.GetUtcNow())));
                _items.Sort((a, b) => b.Task.UpdatedAt.CompareTo(a.Task.UpdatedAt));
                _nextPage = result.NextPage;
                _loaded = true;
                HasMoreItems = _nextPage is not null;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or IOException or OperationCanceledException)
        {
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested)
                {
                    return;
                }

                _error = ex is OperationCanceledException ? "GitHub took too long to respond. Try refreshing agents." : ex.Message;
                _loaded = true;
            }
        }
        finally
        {
            lock (_lock)
            {
                if (generation == _generation && !_disposed)
                {
                    _fetching = false;
                    IsLoading = false;
                    RaiseItemsChanged();
                }
            }
        }
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
            IsLoading = false;
        }

        RaiseItemsChanged();
    }

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _fetching = false;
    }
}
