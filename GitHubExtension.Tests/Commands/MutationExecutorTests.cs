// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Commands;

[TestClass]
public class MutationExecutorTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static Task<bool> Valid(CancellationToken _) => Task.FromResult(true);

    [TestMethod]
    public async Task DuplicateSubmission_SharesTheInFlightWrite()
    {
        var auth = Auth();
        using var executor = new MutationExecutor(auth);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MutationResult<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        Task<MutationResult<int>> Submit(CancellationToken _)
        {
            Interlocked.Increment(ref writes);
            started.TrySetResult();
            return finish.Task;
        }

        var first = executor.ExecuteAsync(Account, "target", Valid, Submit);
        await started.Task;
        var second = executor.ExecuteAsync(Account, "target", Valid, Submit);
        finish.SetResult(new(MutationState.Completed, 42));

        Assert.AreEqual(42, (await first).Value);
        Assert.AreEqual(42, (await second).Value);
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SessionChangeOrDisposal_CancelsAndSuppressesLateSuccess(bool dispose)
    {
        var auth = Auth();
        using var executor = new MutationExecutor(auth);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MutationResult<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = executor.ExecuteAsync(Account, "target", Valid, token =>
        {
            started.SetResult(token);
            return finish.Task;
        });
        var token = await started.Task;
        if (dispose)
        {
            executor.Dispose();
        }
        else
        {
            await auth.SignInWithTokenAsync("https://github.com", "test-token", CancellationToken.None);
            Assert.AreEqual(Account, auth.CurrentAccount);
            Assert.AreNotSame(Account, auth.CurrentAccount);
        }

        Assert.IsTrue(token.IsCancellationRequested);
        finish.SetResult(new(MutationState.Completed, 1));
        Assert.AreEqual(MutationState.Stale, (await operation).State);
    }

    [TestMethod]
    public async Task Reauthentication_DoesNotBlindlyRetryCancelledWrite()
    {
        var auth = Auth();
        using var executor = new MutationExecutor(auth);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MutationResult<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        Task<MutationResult<int>> Submit(CancellationToken _)
        {
            writes++;
            started.TrySetResult();
            return finish.Task;
        }

        var old = executor.ExecuteAsync(Account, "target", Valid, Submit);
        await started.Task;
        await auth.SignInWithTokenAsync("https://github.com", "test-token", CancellationToken.None);
        var result = await executor.ExecuteAsync(auth.CurrentAccount!, "target", Valid, Submit);
        Assert.AreEqual(MutationState.Unknown, result.State);
        Assert.AreEqual(1, writes);
        finish.SetResult(new(MutationState.Completed, 1));
        Assert.AreEqual(MutationState.Stale, (await old).State);
    }

    [TestMethod]
    public async Task FreshValidationFailure_DoesNotWrite()
    {
        using var executor = new MutationExecutor(Auth());
        var called = false;
        var result = await executor.ExecuteAsync(Account, "target", _ => Task.FromResult(false), _ =>
        {
            called = true;
            return Task.FromResult(new MutationResult<int>(MutationState.Completed, 1));
        });
        Assert.IsFalse(called);
        Assert.AreEqual(MutationState.Failed, result.State);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallerCancellationBeforeSubmit_IsVisibleAndAllowsSafeRetry(bool duringValidation)
    {
        using var executor = new MutationExecutor(Auth());
        using var cancellation = new CancellationTokenSource();
        var writes = 0;
        Task<bool> Validate(CancellationToken token)
        {
            if (duringValidation)
            {
                cancellation.Cancel();
            }

            return Task.FromResult(true);
        }

        Task<MutationResult<int>> Submit(CancellationToken _)
        {
            writes++;
            return Task.FromResult(new MutationResult<int>(MutationState.Completed, 3));
        }

        if (!duringValidation)
        {
            cancellation.Cancel();
        }

        var cancelled = await executor.ExecuteAsync(Account, "target", Validate, Submit, cancellationToken: cancellation.Token);
        Assert.AreEqual(MutationState.Failed, cancelled.State);
        Assert.Contains("No request was sent", cancelled.Error ?? string.Empty);
        Assert.AreEqual(0, writes);
        var retry = await executor.ExecuteAsync(Account, "target", Valid, Submit);
        Assert.AreEqual(MutationState.Completed, retry.State);
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public async Task CallerCancellationAfterSubmit_IsUnknownAndBlocksRetryEvenIfClientIgnoresCancellation()
    {
        using var executor = new MutationExecutor(Auth());
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<MutationResult<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        Task<MutationResult<int>> Submit(CancellationToken token)
        {
            writes++;
            started.TrySetResult(token);
            return response.Task;
        }

        var pending = executor.ExecuteAsync(Account, "target", Valid, Submit, cancellationToken: cancellation.Token);
        var token = await started.Task;
        cancellation.Cancel();
        var cancelled = await pending;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(MutationState.Unknown, cancelled.State);
        Assert.Contains("does not undo", cancelled.Error ?? string.Empty);
        Assert.AreEqual(MutationState.Unknown, (await executor.ExecuteAsync(Account, "target", Valid, Submit)).State);
        Assert.AreEqual(1, writes);
        response.SetResult(new(MutationState.Completed, 3));
        Assert.AreEqual(MutationState.Unknown, (await executor.ExecuteAsync(Account, "target", Valid, Submit)).State);
        var reconciled = await executor.ExecuteAsync(Account, "target", Valid, Submit,
            _ => Task.FromResult(new MutationResult<int>(MutationState.Completed, 3)));
        Assert.AreEqual(MutationState.Completed, reconciled.State);
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public async Task ApiFailure_PreservesSsoLinkAndAllowsDefinitiveRetry()
    {
        using var executor = new MutationExecutor(Auth());
        var authorize = new Uri("https://github.com/orgs/example/sso");
        var failure = await executor.ExecuteAsync<int>(Account, "target", Valid,
            _ => Task.FromException<MutationResult<int>>(new GitHubApiException("Authorize access.", authorizeUrl: authorize)));
        Assert.AreEqual(MutationState.Failed, failure.State);
        Assert.AreEqual(authorize, failure.AuthorizeUrl);
        var retry = await executor.ExecuteAsync(Account, "target", Valid,
            _ => Task.FromResult(new MutationResult<int>(MutationState.Completed, 7)));
        Assert.AreEqual(7, retry.Value);
    }

    [TestMethod]
    public async Task AmbiguousWrite_RequiresReconciliationAndNeverBlindlyRetries()
    {
        using var executor = new MutationExecutor(Auth());
        var writes = 0;
        Task<MutationResult<int>> Submit(CancellationToken _)
        {
            writes++;
            return Task.FromException<MutationResult<int>>(new GitHubApiException("Response lost.", outcomeUnknown: true));
        }

        Assert.AreEqual(MutationState.Unknown, (await executor.ExecuteAsync(Account, "target", Valid, Submit)).State);
        Assert.AreEqual(MutationState.Unknown, (await executor.ExecuteAsync(Account, "target", Valid, Submit)).State);
        Assert.AreEqual(MutationState.Pending, (await executor.ExecuteAsync(Account, "target", Valid, Submit,
            _ => Task.FromResult(new MutationResult<int>(MutationState.Pending)))).State);
        var complete = await executor.ExecuteAsync(Account, "target", Valid, Submit,
            _ => Task.FromResult(new MutationResult<int>(MutationState.Completed, 9)));
        Assert.AreEqual(9, complete.Value);
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReconciliationReadFailure_DoesNotClearUncertainOutcome(bool returnedFailure)
    {
        using var executor = new MutationExecutor(Auth());
        var writes = 0;
        Task<MutationResult<int>> Submit(CancellationToken _)
        {
            writes++;
            return Task.FromResult(new MutationResult<int>(MutationState.Pending));
        }

        await executor.ExecuteAsync(Account, "target", Valid, Submit);
        await executor.ExecuteAsync(Account, "target", Valid, Submit,
            _ => returnedFailure
                ? Task.FromResult(new MutationResult<int>(MutationState.Failed, Error: "Read failed."))
                : Task.FromException<MutationResult<int>>(new GitHubApiException("Read failed.")));
        Assert.AreEqual(MutationState.Unknown, (await executor.ExecuteAsync(Account, "target", Valid, Submit)).State);
        Assert.AreEqual(1, writes);
    }

    private static AuthService Auth()
    {
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("octocat");
        return new AuthService(new InMemoryAccountStore(Account), client.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    }
}
