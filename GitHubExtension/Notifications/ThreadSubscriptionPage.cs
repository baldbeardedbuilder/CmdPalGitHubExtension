// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal sealed partial class ThreadSubscriptionPage : ListPage, IDisposable
{
    private readonly IThreadSubscriptionsClient _client;
    private readonly MutationExecutor _executor;
    private readonly GitHubAccount _account;
    private readonly string _threadId;
    private readonly Func<bool> _isCurrent;
    private readonly IBrowserLauncher _browser;
    private readonly ListLoadState _load = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ThreadSubscription? _subscription;
    private string _message = "Loading this thread's subscription...";
    private Uri? _authorizeUrl;
    private bool _mutating;
    private bool _started;

    internal ThreadSubscriptionPage(IThreadSubscriptionsClient client, MutationExecutor executor, GitHubAccount account,
        string threadId, Func<bool> isCurrent, IBrowserLauncher browser)
    {
        _client = client;
        _executor = executor;
        _account = account;
        _threadId = threadId;
        _isCurrent = isCurrent;
        _browser = browser;
        Id = $"com.baldbeardedbuilder.cmdpal.github.subscription.{Uri.EscapeDataString(threadId)}";
        Name = "Manage thread subscription";
        Title = "Thread subscription";
        Icon = Icons.Notifications;
    }

    internal Task CurrentLoad => _load.CurrentLoad;
    internal ThreadSubscription? Subscription { get { lock (_load.SyncRoot) { return _subscription; } } }
    internal bool IsCurrent() => !_load.Disposed && _executor.IsCurrent(_account) && _isCurrent();

    public override IListItem[] GetItems()
    {
        if (!IsCurrent())
        {
            return [];
        }

        bool needsLoad;
        ThreadSubscription? subscription;
        string message;
        Uri? authorize;
        lock (_load.SyncRoot)
        {
            needsLoad = !_started && _load.NeedsLoad && !_mutating;
            subscription = _mutating ? null : _subscription;
            message = _load.Error ?? _message;
            authorize = _load.AuthorizeUrl ?? _authorizeUrl;
        }

        if (needsLoad)
        {
            _ = RefreshAsync();
        }

        var items = new List<IListItem>
        {
            new ListItem(new RefreshCommand(this))
            {
                Title = subscription is null ? "Subscription unknown" : subscription.Ignored ? "Ignored"
                    : subscription.Subscribed ? "Subscribed" : "Repository notification rules apply",
                Subtitle = message,
            },
        };
        if (subscription is not null)
        {
            foreach (var action in Enum.GetValues<ThreadSubscriptionAction>())
            {
                items.Add(new ListItem(Confirmation(subscription, action)) { Title = ActionName(action), Icon = Icons.Notifications });
            }
        }

        if (authorize is not null)
        {
            items.Add(new ListItem(new OpenInBrowserCommand(_browser, authorize, "Authorize organization access", Icons.Authorize)));
        }

        return [.. items];
    }

    internal MutationConfirmationPage Confirmation(ThreadSubscription expected, ThreadSubscriptionAction action) =>
        new(_account, ActionName(action), $"Notification thread {_threadId}", action switch
        {
            ThreadSubscriptionAction.Subscribe => "Subscribe to this conversation and clear any ignore setting. This doesn't mark it read or done.",
            ThreadSubscriptionAction.Ignore => "Ignore this conversation, including notifications from a watched repository, until you comment or get an @mention. This doesn't mark it read or done.",
            _ => "Remove your explicit thread subscription. A watched repository can still notify you; use Ignore to stop those notifications. This doesn't mark it read or done.",
        }, () => SetAsync(expected, action), Feedback, IsCurrent);

    private (string Message, Uri? AuthorizeUrl) Feedback()
    {
        lock (_load.SyncRoot)
        {
            return (_message, _authorizeUrl);
        }
    }

    internal async Task SetAsync(ThreadSubscription expected, ThreadSubscriptionAction action, CancellationToken cancellationToken = default)
    {
        if (!IsCurrent())
        {
            return;
        }

        ThreadSubscription? observed = null;
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
        async Task<MutationResult<ThreadSubscription>> Reconcile(CancellationToken token)
        {
            observed = await _client.GetSubscriptionAsync(_account, _threadId, token).ConfigureAwait(false);
            var complete = action switch
            {
                ThreadSubscriptionAction.Subscribe => observed.Subscribed && !observed.Ignored,
                ThreadSubscriptionAction.Ignore => observed.Ignored,
                _ => !observed.Subscribed && !observed.Ignored,
            };
            return complete ? new(MutationState.Completed, observed)
                : new(MutationState.Pending, Error: "GitHub hasn't confirmed the requested subscription. Refresh before trying again.");
        }

        var result = await _executor.ExecuteAsync(_account, $"subscription:{_threadId}",
            async token =>
            {
                observed = await _client.GetSubscriptionAsync(_account, _threadId, token).ConfigureAwait(false);
                return IsCurrent() && observed == expected;
            },
            async token =>
            {
                observed = null;
                await _client.SetSubscriptionAsync(_account, _threadId, action, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!IsCurrent())
                {
                    return new MutationResult<ThreadSubscription>(MutationState.Stale);
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
            _subscription = observed;
            _message = result.State == MutationState.Completed
                ? "GitHub confirmed the subscription change. Read and done state haven't changed."
                : result.Error ?? "Couldn't update this subscription. Refresh to check GitHub before retrying.";
            _authorizeUrl = result.AuthorizeUrl;
        }

        RaiseItemsChanged();
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
            var subscription = await _client.GetSubscriptionAsync(_account, _threadId, operation.Token).ConfigureAwait(false);
            lock (_load.SyncRoot)
            {
                if (!_load.IsCurrent(operation) || !IsCurrent())
                {
                    return;
                }

                _subscription = subscription;
                _message = "Unsubscribe restores repository rules. Ignore stops this thread's future notifications.";
                _authorizeUrl = null;
                _load.Succeed(operation, null);
            }
        }, () =>
        {
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to return this subscription. Try refreshing.", area: DiagnosticArea.Notifications);
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
            _subscription = null;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private static string ActionName(ThreadSubscriptionAction action) => action switch
    {
        ThreadSubscriptionAction.Subscribe => "Subscribe to thread",
        ThreadSubscriptionAction.Ignore => "Ignore thread",
        _ => "Unsubscribe from thread",
    };

    private sealed partial class RefreshCommand(ThreadSubscriptionPage page) : InvokableCommand
    {
        public override string Name { get; set; } = "Refresh subscription";
        public override ICommandResult Invoke()
        {
            _ = page.RefreshAsync();
            return CommandResult.KeepOpen();
        }
    }
}
