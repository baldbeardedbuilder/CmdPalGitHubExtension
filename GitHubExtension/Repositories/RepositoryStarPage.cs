// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal sealed partial class RepositoryStarPage : ListPage, IDisposable
{
    private readonly IRepositoryStarsClient _client;
    private readonly MutationExecutor _executor;
    private readonly GitHubAccount _account;
    private readonly string _repository;
    private readonly Func<bool> _isCurrent;
    private readonly IBrowserLauncher _browser;
    private readonly Func<Task>? _changed;
    private readonly ListLoadState _load = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PageEmptyContent _empty;
    private bool? _starred;
    private string _message = "Loading your star state...";
    private Uri? _authorizeUrl;
    private bool _mutating;
    private bool _started;

    internal RepositoryStarPage(IRepositoryStarsClient client, MutationExecutor executor, GitHubAccount account,
        string repository, Func<bool> isCurrent, IBrowserLauncher browser, Func<Task>? changed = null)
    {
        _client = client;
        _executor = executor;
        _account = account;
        _repository = repository;
        _isCurrent = isCurrent;
        _browser = browser;
        _changed = changed;
        Id = $"com.baldbeardedbuilder.cmdpal.github.star.{Uri.EscapeDataString(repository)}";
        Name = "Manage star";
        Title = repository;
        Icon = Icons.Repos;
        _empty = new PageEmptyContent(Icons.Repos, new RefreshCommand(this));
    }

    internal Task CurrentLoad => _load.CurrentLoad;

    public override IListItem[] GetItems()
    {
        if (!IsCurrent())
        {
            return [];
        }

        bool needsLoad;
        bool? starred;
        string message;
        Uri? authorize;
        lock (_load.SyncRoot)
        {
            needsLoad = !_started && _load.NeedsLoad && !_mutating;
            starred = _mutating ? null : _starred;
            message = _load.Error ?? _message;
            authorize = _load.AuthorizeUrl ?? _authorizeUrl;
        }

        if (needsLoad)
        {
            _ = RefreshAsync();
        }

        EmptyContent = _empty.Get("Couldn't load your star state", message, refresh: true);
        var items = new List<IListItem>
        {
            new ListItem(new RefreshCommand(this)) { Title = starred is null ? "Star state unknown" : starred.Value ? "Starred" : "Not starred", Subtitle = message },
        };
        if (starred is { } state)
        {
            items.Add(new ListItem(Confirmation(state)) { Title = state ? "Unstar repository" : "Star repository", Icon = Icons.Repos });
        }

        if (authorize is not null)
        {
            items.Add(new ListItem(new OpenInBrowserCommand(_browser, authorize, "Authorize organization access", Icons.Authorize)));
        }

        return [.. items];
    }

    internal bool IsCurrent() => !_load.Disposed && _executor.IsCurrent(_account) && _isCurrent();

    internal MutationConfirmationPage Confirmation(bool expected) =>
        new(_account, expected ? "Unstar repository" : "Star repository", _repository,
            expected ? "Remove this repository from your starred list." : "Save this repository to your starred list.",
            () => SetAsync(expected), Feedback, IsCurrent);

    private (string Message, Uri? AuthorizeUrl) Feedback()
    {
        lock (_load.SyncRoot)
        {
            return (_message, _authorizeUrl);
        }
    }

    internal async Task SetAsync(bool expected, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent())
        {
            return;
        }

        var desired = !expected;
        var target = $"star:{_repository.ToLowerInvariant()}";
        CancellationTokenSource operation;
        lock (_load.SyncRoot)
        {
            if (_mutating || _load.Disposed)
            {
                return;
            }

            _mutating = true;
            _started = true;
            _load.Invalidate(reset: true);
            operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        }

        using var operationLifetime = operation;
        bool? observed = null;
        async Task<MutationResult<bool>> Reconcile(CancellationToken token)
        {
            observed = await _client.IsStarredAsync(_account, _repository, token).ConfigureAwait(false);
            return observed == desired
                ? new(MutationState.Completed, desired)
                : new(MutationState.Pending, Error: "GitHub hasn't confirmed the requested star state. Refresh before trying again.");
        }

        var result = await _executor.ExecuteAsync(_account, target,
            async token =>
            {
                observed = await _client.IsStarredAsync(_account, _repository, token).ConfigureAwait(false);
                return IsCurrent() && observed == expected;
            },
            async token =>
            {
                observed = null;
                await _client.SetStarredAsync(_account, _repository, desired, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent())
                {
                    return new MutationResult<bool>(MutationState.Stale);
                }

                try
                {
                    return await Reconcile(token).ConfigureAwait(false);
                }
                catch (GitHubApiException ex)
                {
                    var error = new GitHubApiException(ex.Message, ex, ex.AuthorizeUrl, outcomeUnknown: true);
                    OperationDiagnostics.CorrelateFailure(ex, error);
                    throw error;
                }
            }, Reconcile, operation.Token).ConfigureAwait(false);
        lock (_load.SyncRoot)
        {
            _mutating = false;
        }

        if (!IsCurrent() || result.State == MutationState.Stale)
        {
            return;
        }

        lock (_load.SyncRoot)
        {
            _starred = observed;
            _message = result.State == MutationState.Completed
                ? desired ? "GitHub confirmed your star." : "GitHub confirmed the repository is no longer starred."
                : result.Error ?? "Couldn't update your star. Refresh to check GitHub before retrying.";
            _authorizeUrl = result.AuthorizeUrl;
        }

        RaiseItemsChanged();
        if (result.State == MutationState.Completed && _changed is not null)
        {
            await _changed().ConfigureAwait(false);
        }
    }

    internal Task RefreshAsync()
    {
        ListLoadState.Operation operation;
        lock (_load.SyncRoot)
        {
            if (!IsCurrent() || _mutating || !_load.TryBegin(true, out operation))
            {
                return CurrentLoad;
            }

            _started = true;
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, async () =>
        {
            var starred = await _client.IsStarredAsync(_account, _repository, operation.Token).ConfigureAwait(false);
            lock (_load.SyncRoot)
            {
                if (!_load.IsCurrent(operation) || !IsCurrent())
                {
                    return;
                }

                _starred = starred;
                _message = "This is your personal star state, not the public star count.";
                _authorizeUrl = null;
                _load.Succeed(operation, null);
            }
        }, () =>
        {
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to return your star state. Try refreshing.", area: DiagnosticArea.Repositories);
    }

    public void Dispose()
    {
        lock (_load.SyncRoot)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            _starred = null;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private sealed partial class RefreshCommand(RepositoryStarPage page) : InvokableCommand
    {
        public override string Name { get; set; } = "Refresh star state";
        public override ICommandResult Invoke()
        {
            _ = page.RefreshAsync();
            return CommandResult.KeepOpen();
        }
    }
}
