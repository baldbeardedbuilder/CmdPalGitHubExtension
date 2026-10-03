// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

// Pages use SyncRoot for both load state and domain state. Publish toolkit notifications only after releasing it.
internal sealed partial class ListLoadState : IDisposable
{
    private Operation? _operation;

    public Lock SyncRoot { get; } = new();

    public bool Loaded { get; private set; }

    public bool Fetching { get; private set; }

    public bool Disposed { get; private set; }

    public bool NeedsLoad => !Disposed && !Loaded && !Fetching;

    public Uri? NextPage { get; private set; }

    public string? Error { get; private set; }

    public Task CurrentLoad { get; private set; } = Task.CompletedTask;

    public bool TryBegin(bool reset, out Operation operation)
    {
        operation = null!;
        if (Disposed || Fetching || (!reset && NextPage is null))
        {
            return false;
        }

        operation = new Operation(reset, reset ? null : NextPage);
        _operation = operation;
        Fetching = true;
        Error = null;
        CurrentLoad = operation.Completion.Task;
        return true;
    }

    public bool IsCurrent(Operation operation) =>
        !Disposed && ReferenceEquals(operation, _operation) && !operation.Token.IsCancellationRequested;

    public void Succeed(Operation operation, Uri? nextPage)
    {
        if (IsCurrent(operation))
        {
            NextPage = nextPage;
            Loaded = true;
            Error = null;
        }
    }

    public Task Run(Operation operation, Func<Task> work, Action completed, string timeoutMessage, bool markLoadedOnError = true)
    {
        lock (SyncRoot)
        {
            _ = Task.Run(async () =>
            {
                Exception? failure = null;
                try
                {
                    bool current;
                    lock (SyncRoot)
                    {
                        current = IsCurrent(operation);
                    }

                    if (current)
                    {
                        await work().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (operation.Token.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or IOException or OperationCanceledException)
                {
                    lock (SyncRoot)
                    {
                        if (IsCurrent(operation))
                        {
                            Error = ex is OperationCanceledException ? timeoutMessage : ex.Message;
                            Loaded |= markLoadedOnError;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    bool publish;
                    lock (SyncRoot)
                    {
                        publish = IsCurrent(operation);
                        if (publish)
                        {
                            Fetching = false;
                        }
                    }

                    try
                    {
                        if (publish)
                        {
                            completed();
                        }
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                    }

                    Task cancelCallbacks;
                    lock (SyncRoot)
                    {
                        operation.Finished = true;
                        cancelCallbacks = operation.CancelCallbacks;
                    }

                    try
                    {
                        await cancelCallbacks.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure ??= ex;
                    }

                    operation.Cancellation.Dispose();
                    if (failure is null)
                    {
                        operation.Completion.TrySetResult();
                    }
                    else
                    {
                        operation.Completion.TrySetException(failure);
                    }
                }
            });
            return CurrentLoad;
        }
    }

    public void Publish(Operation operation, Action notification)
    {
        lock (SyncRoot)
        {
            if (!IsCurrent(operation))
            {
                return;
            }
        }

        notification();
    }

    public void Invalidate(bool reset = false)
    {
        if (_operation is { } operation)
        {
            // CancelAsync marks the token immediately without running client callbacks under the page lock.
            if (!operation.Finished)
            {
                operation.CancelCallbacks = operation.Cancellation.CancelAsync();
            }
            _operation = null;
        }

        Fetching = false;
        if (reset)
        {
            NextPage = null;
            Loaded = false;
            Error = null;
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            Disposed = true;
            Invalidate();
        }
    }

    internal sealed class Operation
    {
        public Operation(bool reset, Uri? page)
        {
            Reset = reset;
            Page = page;
            Token = Cancellation.Token;
        }

        internal CancellationTokenSource Cancellation { get; } = new();

        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task CancelCallbacks { get; set; } = Task.CompletedTask;

        internal bool Finished { get; set; }

        public CancellationToken Token { get; }

        public bool Reset { get; }

        public Uri? Page { get; }
    }
}
