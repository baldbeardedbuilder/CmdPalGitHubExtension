// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal enum MutationState
{
    Completed,
    Pending,
    Unknown,
    Failed,
    RetryAllowed,
    Stale,
}

internal sealed record MutationResult<T>(MutationState State, T? Value = default, string? Error = null, Uri? AuthorizeUrl = null);

/// <summary>
/// Owns mutations for a page session. An uncertain write is never blindly submitted again.
/// Reconciliation must explicitly prove completion or prove that retrying is safe.
/// </summary>
internal sealed partial class MutationExecutor : IDisposable
{
    private readonly AuthService _auth;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task<object>> _running = [];
    private readonly HashSet<string> _uncertain = [];
    private readonly HashSet<string> _submitted = [];
    private CancellationTokenSource _session = new();
    private int _generation;
    private bool _disposed;

    public MutationExecutor(AuthService auth)
    {
        _auth = auth;
        _auth.AccountChanged += OnAccountChanged;
    }

    public bool IsCurrent(GitHubAccount account)
    {
        lock (_lock)
        {
            return !_disposed && ReferenceEquals(account, _auth.CurrentAccount);
        }
    }

    // Call only after an authoritative read proves the requested final state.
    public void ObserveCompletion(GitHubAccount account, string target)
    {
        lock (_lock)
        {
            if (IsCurrent(account))
            {
                _uncertain.Remove(Key(account, target));
            }
        }
    }

    public Task<MutationResult<T>> ExecuteAsync<T>(
        GitHubAccount account,
        string target,
        Func<CancellationToken, Task<bool>> validate,
        Func<CancellationToken, Task<MutationResult<T>>> submit,
        Func<CancellationToken, Task<MutationResult<T>>>? reconcile = null,
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!IsCurrent(account))
            {
                return Task.FromResult(new MutationResult<T>(MutationState.Stale));
            }

            // Include the host even when callers use a relative resource identifier.
            var key = Key(account, target);
            if (_running.TryGetValue(key, out var running))
            {
                return Unbox<T>(running);
            }

            var generation = _generation;
            var sessionToken = _session.Token;
            var uncertain = _uncertain.Contains(key);
            var task = Task.Run<object>(async () =>
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
                var token = operation.Token;
                var submitted = false;
                MutationResult<T> result;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (uncertain)
                    {
                        result = reconcile is null
                            ? new(MutationState.Unknown, Error: "The previous request may have succeeded. Check GitHub before trying again; automatic retry is disabled.")
                            : await reconcile(token).WaitAsync(token).ConfigureAwait(false);
                        if (result.State == MutationState.Failed)
                        {
                            result = result with { State = MutationState.Unknown };
                        }

                        if (result.State != MutationState.RetryAllowed)
                        {
                            return Finish(key, account, generation, result);
                        }
                    }

                    if (!IsCurrent(account) || !await validate(token).WaitAsync(token).ConfigureAwait(false))
                    {
                        return Finish(key, account, generation, new MutationResult<T>(MutationState.Failed, Error: "This target changed. Refresh and review the action again."));
                    }

                    token.ThrowIfCancellationRequested();
                    if (!IsCurrent(account))
                    {
                        return Finish(key, account, generation, new MutationResult<T>(MutationState.Stale));
                    }

                    lock (_lock)
                    {
                        if (generation != _generation || !IsCurrent(account))
                        {
                            return new MutationResult<T>(MutationState.Stale);
                        }

                        _submitted.Add(key);
                        submitted = true;
                    }

                    result = await submit(token).WaitAsync(token).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result = new(MutationState.Unknown, Error: "Cancelled after submission. GitHub may still complete the request; check its state before retrying.");
                    }
                }
                catch (OperationCanceledException) when (sessionToken.IsCancellationRequested)
                {
                    result = new(MutationState.Stale);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result = submitted || uncertain
                        ? new(MutationState.Unknown, Error: "Cancelled after a request may have been sent. Check GitHub before retrying; cancellation does not undo the request.")
                        : new(MutationState.Failed, Error: "Cancelled before submitting. No request was sent.");
                }
                catch (GitHubApiException ex)
                {
                    result = new(ex.OutcomeUnknown || uncertain || (submitted && cancellationToken.IsCancellationRequested)
                        ? MutationState.Unknown : MutationState.Failed, Error: ex.Message, AuthorizeUrl: ex.AuthorizeUrl);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
                {
                    result = submitted || uncertain
                        ? new(MutationState.Unknown, Error: "The request may have succeeded. Refresh to check GitHub before trying again.")
                        : new(MutationState.Failed, Error: "Couldn't verify the target. No request was sent; refresh before trying again.");
                }
                catch (Exception)
                {
                    result = submitted || uncertain
                        ? new(MutationState.Unknown, Error: "Couldn't complete this request. Check GitHub before retrying.")
                        : new(MutationState.Failed, Error: "Couldn't verify the target. No request was sent; refresh before trying again.");
                }
                return Finish(key, account, generation, result);
            });
            _running[key] = task;
            return Unbox<T>(task);
        }
    }

    private MutationResult<T> Finish<T>(string key, GitHubAccount account, int generation, MutationResult<T> result)
    {
        lock (_lock)
        {
            if (generation != _generation || !IsCurrent(account))
            {
                return new(MutationState.Stale);
            }

            _running.Remove(key);
            _submitted.Remove(key);
            if (result.State is MutationState.Unknown or MutationState.Pending)
            {
                _uncertain.Add(key);
            }
            else
            {
                _uncertain.Remove(key);
            }

            return result;
        }
    }

    private static async Task<MutationResult<T>> Unbox<T>(Task<object> task) =>
        (MutationResult<T>)await task.ConfigureAwait(false);

    private static string Key(GitHubAccount account, string target) =>
        $"{account.Host.ApiUrl}|{account.Login.ToLowerInvariant()}|{target}";

    private void OnAccountChanged(object? sender, EventArgs e) => Reset(false);

    private void Reset(bool dispose)
    {
        CancellationTokenSource previous;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed |= dispose;
            _generation++;
            previous = _session;
            _session = new CancellationTokenSource();
            // Cancellation cannot prove that a request already sent to GitHub was undone.
            _uncertain.UnionWith(_submitted);
            _submitted.Clear();
            _running.Clear();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
        }

        _auth.AccountChanged -= OnAccountChanged;
        Reset(true);
        _session.Dispose();
    }
}
