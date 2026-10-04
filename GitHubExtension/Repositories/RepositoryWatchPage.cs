using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal sealed partial class RepositoryWatchPage : ListPage, IDisposable
{
    private readonly IRepositoryWatchingClient _client;
    private readonly MutationExecutor _executor;
    private readonly GitHubAccount _account;
    private readonly string _repository;
    private readonly Func<bool> _isCurrent;
    private readonly IBrowserLauncher _browser;
    private readonly ListLoadState _load = new();
    private readonly CancellationTokenSource _lifetime = new();
    private RepositorySubscription? _state;
    private string _message = "Refresh to check your watching state.";
    private Uri? _authorize;
    private bool _mutating;
    private readonly PageEmptyContent _empty;

    internal RepositoryWatchPage(IRepositoryWatchingClient client, MutationExecutor executor, GitHubAccount account,
        string repository, Func<bool> isCurrent, IBrowserLauncher browser)
    {
        (_client, _executor, _account, _repository, _isCurrent, _browser) = (client, executor, account, repository, isCurrent, browser);
        Name = "Manage watching";
        Title = repository;
        Icon = Icons.Repos;
        _empty = new(Icons.Repos, new RefreshCommand(this));
    }

    internal Task CurrentLoad => _load.CurrentLoad;
    internal bool IsCurrent() => !_load.Disposed && _executor.IsCurrent(_account) && _isCurrent();

    public override IListItem[] GetItems()
    {
        if (!IsCurrent()) { return []; }
        bool start;
        RepositorySubscription? state;
        string message;
        Uri? authorize;
        lock (_load.SyncRoot)
        {
            start = _load.NeedsLoad && !_mutating;
            state = _mutating ? null : _state;
            message = _load.Error ?? _message;
            authorize = _load.AuthorizeUrl ?? _authorize;
        }

        if (start) { _ = RefreshAsync(); }
        EmptyContent = _empty.Get("Watching state unavailable", message, refresh: true);
        var items = new List<IListItem>
        {
            new ListItem(new RefreshCommand(this))
            {
                Title = state is null ? "Watching state unknown" : state.Ignored ? "Ignored" : state.Subscribed ? "Watching" : "Not watching",
                Subtitle = message,
            },
        };
        if (state is not null)
        {
            foreach (var action in Enum.GetValues<RepositoryWatchAction>())
            {
                items.Add(new ListItem(Confirmation(state, action)) { Title = Label(action), Icon = Icons.Repos });
            }
        }

        if (authorize is not null) { items.Add(new ListItem(new OpenInBrowserCommand(_browser, authorize, "Authorize organization access", Icons.Authorize))); }
        return [.. items];
    }

    internal MutationConfirmationPage Confirmation(RepositorySubscription expected, RepositoryWatchAction action) =>
        new(_account, Label(action), _repository,
            action == RepositoryWatchAction.Ignore ? "Ignore notifications from this repository."
            : action == RepositoryWatchAction.Watch ? "Subscribe to repository notifications. This doesn't configure custom notification categories."
            : "Remove your repository subscription, including any ignored state.",
            () => SetAsync(expected, action), () => (_message, _authorize), IsCurrent);

    internal async Task SetAsync(RepositorySubscription expected, RepositoryWatchAction action, CancellationToken token = default)
    {
        if (!IsCurrent()) { return; }
        lock (_load.SyncRoot)
        {
            if (_mutating) { return; }
            _mutating = true;
            _load.Invalidate();
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var desired = new RepositorySubscription(action == RepositoryWatchAction.Watch, action == RepositoryWatchAction.Ignore);
        RepositorySubscription? observed = null;
        async Task<MutationResult<bool>> Check(CancellationToken cancellation)
        {
            observed = await _client.GetWatchingAsync(_account, _repository, cancellation).ConfigureAwait(false);
            return observed == desired ? new(MutationState.Completed, true)
                : new(MutationState.Pending, Error: "GitHub hasn't confirmed that watching state. Refresh before trying again.");
        }

        var result = await _executor.ExecuteAsync(_account, $"watch:{_repository.ToLowerInvariant()}",
            async cancellation =>
            {
                if (!IsCurrent()) { return false; }
                var current = await _client.GetWatchingAsync(_account, _repository, cancellation).ConfigureAwait(false);
                return IsCurrent() && current == expected;
            },
            async cancellation =>
            {
                if (!IsCurrent()) { return new(MutationState.Stale); }
                await _client.SetWatchingAsync(_account, _repository, action, cancellation).ConfigureAwait(false);
                try { return await Check(cancellation).ConfigureAwait(false); }
                catch (GitHubApiException ex) { throw new GitHubApiException(ex.Message, ex, ex.AuthorizeUrl, outcomeUnknown: true); }
            }, Check, lifetime.Token).ConfigureAwait(false);
        lock (_load.SyncRoot)
        {
            _mutating = false;
            if (!IsCurrent()) { return; }
            _state = observed;
            _message = result.State == MutationState.Completed ? "GitHub confirmed your watching state."
                : result.Error ?? "Refresh to check GitHub before retrying.";
            _authorize = result.AuthorizeUrl;
        }

        RaiseItemsChanged();
    }

    internal Task RefreshAsync()
    {
        ListLoadState.Operation operation;
        lock (_load.SyncRoot)
        {
            if (!IsCurrent() || _mutating || !_load.TryBegin(true, out operation)) { return CurrentLoad; }
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, async () =>
        {
            var state = await _client.GetWatchingAsync(_account, _repository, operation.Token).ConfigureAwait(false);
            lock (_load.SyncRoot)
            {
                if (!_load.IsCurrent(operation) || !IsCurrent()) { return; }
                _state = state;
                _message = "Your subscription, not the repository's public watcher count.";
                _authorize = null;
                _load.Succeed(operation, null);
            }
        }, () =>
        {
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to return your watching state.", area: DiagnosticArea.Repositories);
    }

    public void Dispose()
    {
        lock (_load.SyncRoot) { _load.Dispose(); _state = null; }
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static string Label(RepositoryWatchAction action) => action switch
    {
        RepositoryWatchAction.Watch => "Watch repository",
        RepositoryWatchAction.Ignore => "Ignore repository",
        _ => "Unwatch repository",
    };

    private sealed partial class RefreshCommand(RepositoryWatchPage page) : InvokableCommand
    {
        public override string Name { get; set; } = "Refresh watching state";
        public override ICommandResult Invoke() { _ = page.RefreshAsync(); return CommandResult.KeepOpen(); }
    }
}
