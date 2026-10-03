// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public sealed class ListLoadStateTests
{
    private static readonly Uri Next = new("https://api.github.com/items?page=2");

    [TestMethod]
    public async Task Pagination_SerializesRequestsAndStopsAtLastPage()
    {
        using var state = new ListLoadState();
        Assert.IsTrue(state.NeedsLoad);
        Assert.IsFalse(state.TryBegin(false, out _));
        Assert.IsTrue(state.TryBegin(true, out var first));
        Assert.IsNull(first.Page);
        Assert.IsFalse(state.NeedsLoad);
        Assert.IsFalse(state.TryBegin(true, out _));
        await state.Run(first, () =>
        {
            state.Succeed(first, Next);
            return Task.CompletedTask;
        }, () => { }, "timeout");

        Assert.IsTrue(state.Loaded);
        Assert.IsFalse(state.Fetching);
        Assert.IsTrue(state.TryBegin(false, out var second));
        Assert.AreEqual(Next, second.Page);
        await state.Run(second, () =>
        {
            state.Succeed(second, null);
            return Task.CompletedTask;
        }, () => { }, "timeout");
        Assert.IsFalse(state.TryBegin(false, out _));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Refresh_IgnoresStaleSuccessAndFailure(bool fail)
    {
        using var state = new ListLoadState();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(state.TryBegin(true, out var old));
        var publications = 0;
        var oldLoad = state.Run(old, async () =>
        {
            started.SetResult();
            await pending.Task;
            if (fail)
            {
                throw new GitHubApiException("stale error");
            }

            state.Succeed(old, Next);
        }, () => publications++, "timeout");
        await started.Task;
        state.Invalidate();
        Assert.IsTrue(old.Token.IsCancellationRequested);
        Assert.IsTrue(state.TryBegin(true, out var fresh));
        pending.SetResult();
        await oldLoad;

        Assert.IsTrue(state.Fetching);
        Assert.IsNull(state.Error);
        Assert.IsNull(state.NextPage);
        Assert.AreEqual(0, publications);
        await state.Run(fresh, () =>
        {
            state.Succeed(fresh, null);
            return Task.CompletedTask;
        }, () => publications++, "timeout");
        Assert.AreEqual(1, publications);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cancellation_AccountResetAndDisposalRejectLateResults(bool dispose)
    {
        using var state = new ListLoadState();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.IsTrue(state.TryBegin(true, out var operation));
        var publications = 0;
        var load = state.Run(operation, async () =>
        {
            started.SetResult();
            await pending.Task;
            state.Succeed(operation, Next);
        }, () => publications++, "timeout");
        await started.Task;
        if (dispose)
        {
            state.Dispose();
        }
        else
        {
            state.Invalidate(reset: true);
        }

        pending.SetResult();
        await load;
        Assert.IsTrue(operation.Token.IsCancellationRequested);
        Assert.IsFalse(state.Loaded);
        Assert.IsFalse(state.Fetching);
        Assert.IsNull(state.NextPage);
        Assert.AreEqual(0, publications);
        Assert.AreEqual(!dispose, state.TryBegin(true, out var next));
        if (!dispose)
        {
            await state.Run(next, () => Task.CompletedTask, () => { }, "timeout");
        }
    }

    [TestMethod]
    public async Task Timeout_IsRetryableAndPaginationFailureKeepsCursor()
    {
        using var state = new ListLoadState();
        Assert.IsTrue(state.TryBegin(true, out var first));
        await state.Run(first, () => throw new TaskCanceledException(), () => { }, "try refreshing");
        Assert.AreEqual("try refreshing", state.Error);
        Assert.IsTrue(state.Loaded);
        Assert.IsFalse(state.NeedsLoad);
        state.Invalidate();
        Assert.IsTrue(state.TryBegin(true, out var retry));
        await state.Run(retry, () =>
        {
            state.Succeed(retry, Next);
            return Task.CompletedTask;
        }, () => { }, "timeout");
        Assert.IsNull(state.Error);
        Assert.IsTrue(state.TryBegin(false, out var more));
        await state.Run(more, () => throw new GitHubApiException("rate limited"), () => { }, "timeout");
        Assert.AreEqual(Next, state.NextPage);
        Assert.AreEqual("rate limited", state.Error);
        Assert.IsFalse(state.Fetching);
        Assert.IsTrue(state.TryBegin(false, out var last));
        await state.Run(last, () => Task.CompletedTask, () => { }, "timeout");
    }

    [TestMethod]
    public async Task Notifications_AreOutsideLockAndStopAfterReentrantReset()
    {
        using var state = new ListLoadState();
        Assert.IsTrue(state.TryBegin(true, out var operation));
        var notifications = 0;
        await state.Run(operation, () => Task.CompletedTask, () =>
        {
            state.Publish(operation, () =>
            {
                var read = Task.Run(() =>
                {
                    lock (state.SyncRoot)
                    {
                        return state.Fetching;
                    }
                });
                Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(2)));
                Assert.IsFalse(read.Result);
                notifications++;
                state.Invalidate(reset: true);
            });
            state.Publish(operation, () => notifications++);
        }, "timeout");
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public async Task InvalidatedBeforeScheduling_CompletesReservedTaskWithoutRequest()
    {
        using var state = new ListLoadState();
        Assert.IsTrue(state.TryBegin(true, out var operation));
        var load = state.CurrentLoad;
        state.Invalidate(reset: true);
        await state.Run(operation, () => throw new AssertFailedException("Must not request"), () => { }, "timeout");
        await load;
    }

    [TestMethod]
    public async Task Cancellation_RunsCallbacksOutsideLockAndDoesNotBecomeError()
    {
        using var state = new ListLoadState();
        Assert.IsTrue(state.TryBegin(true, out var operation));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = operation.Token.Register(() =>
        {
            lock (state.SyncRoot)
            {
                callback.SetResult();
            }
        });
        var load = state.Run(operation, async () =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, operation.Token);
        }, () => Assert.Fail("Canceled operations must not publish"), "timeout");
        await started.Task;
        lock (state.SyncRoot)
        {
            state.Invalidate(reset: true);
        }

        await callback.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(state.Error);
        Assert.IsFalse(state.Fetching);
    }
}
