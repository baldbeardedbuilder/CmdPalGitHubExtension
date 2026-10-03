// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues.IssueMutationTestData;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueMutationSessionTests
{
    private static readonly string[] ExpectedPagedLabels = ["bug", "help wanted"];
    public TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("Closed")]
    [DataRow("NotPlanned")]
    [DataRow("Open")]
    public async Task StateChange_VerifiesFreshStateAndRefreshesMetadata(string stateName)
    {
        var state = Enum.Parse<SubjectState>(stateName);
        var reviewed = Issue(state == SubjectState.Open ? SubjectState.Closed : SubjectState.Open);
        var updated = reviewed with { State = state, Title = "Updated title" };
        var client = Client(reviewed);
        client.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reviewed).ReturnsAsync(updated);
        client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, state, It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        using var session = new IssueMutationSession(CreateAuth(), client.Object);

        var result = await session.ExecuteAsync(Account, "octo/tool", reviewed, new(IssueChangeKind.State, State: state), () => true, TestContext.CancellationToken);

        Assert.AreEqual(MutationState.Completed, result.State);
        Assert.AreEqual(updated, result.Value);
        client.Verify(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task StateRace_RejectsChangedStateWithoutWriting()
    {
        var client = Client(Issue(SubjectState.NotPlanned));
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var result = await session.ExecuteAsync(Account, "octo/tool", Issue(SubjectState.Closed),
            new(IssueChangeKind.State, State: SubjectState.Open), () => true, TestContext.CancellationToken);

        Assert.AreEqual(MutationState.Failed, result.State);
        client.Verify(c => c.ChangeStateAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SubjectState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [Timeout(10000)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AssigneeChange_PreservesOthersAndVerifiesReturnedAssignment(bool add)
    {
        var before = Issue(assignees: add ? ["mona", "hubot"] : ["mona", "hubot", "octocat"]);
        var after = before with { Assignees = add ? ["mona", "hubot", "octocat"] : ["mona", "hubot"] };
        var client = Client(before);
        client.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>())).ReturnsAsync(before).ReturnsAsync(after);
        client.Setup(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", add, It.IsAny<CancellationToken>())).ReturnsAsync(after);
        using var session = new IssueMutationSession(CreateAuth(), client.Object);

        var result = await session.ExecuteAsync(Account, "octo/tool", before, new(IssueChangeKind.Assignee, "octocat", add), () => true, TestContext.CancellationToken);

        Assert.AreEqual(MutationState.Completed, result.State);
        CollectionAssert.AreEquivalent(after.Assignees.ToArray(), result.Value!.Assignees.ToArray());
        client.Verify(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", add, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LabelChange_UsesFreshBaselineAndPreservesConcurrentLabels(bool add)
    {
        var reviewed = Issue(labels: add ? ["bug"] : ["bug", "help wanted"]);
        var before = reviewed with { Labels = add ? ["bug", "concurrent"] : ["bug", "help wanted", "concurrent"] };
        var after = before with { Labels = add ? ["bug", "concurrent", "help wanted"] : ["bug", "concurrent"] };
        var client = Client(before);
        client.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(before).ReturnsAsync(after).ReturnsAsync(after);
        client.Setup(c => c.ChangeLabelAsync(Account, "octo/tool", 42, "help wanted", add, It.IsAny<CancellationToken>())).ReturnsAsync(after.Labels);
        using var session = new IssueMutationSession(CreateAuth(), client.Object);

        var result = await session.ExecuteAsync(Account, "octo/tool", reviewed, new(IssueChangeKind.Label, "help wanted", add), () => true, TestContext.CancellationToken);

        Assert.AreEqual(MutationState.Completed, result.State);
        Assert.Contains("concurrent", result.Value!.Labels);
    }

    [TestMethod]
    [DataRow("ignored")]
    [DataRow("lost-other")]
    [DataRow("wrong-target")]
    public async Task AssigneeWrite_UnverifiedResultIsUnknownAndNotRetried(string scenario)
    {
        var before = Issue();
        var after = scenario switch
        {
            "ignored" => before,
            "lost-other" => before with { Assignees = ["octocat"] },
            _ => before with { Number = 99, Assignees = ["mona", "octocat"] },
        };
        var client = Client(before);
        client.Setup(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", true, It.IsAny<CancellationToken>())).ReturnsAsync(after);
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var change = new IssueChange(IssueChangeKind.Assignee, "octocat");

        Assert.AreEqual(MutationState.Unknown, (await session.ExecuteAsync(Account, "octo/tool", before, change, () => true, TestContext.CancellationToken)).State);
        Assert.AreEqual(MutationState.Unknown, (await session.ExecuteAsync(Account, "octo/tool", before, change, () => true, TestContext.CancellationToken)).State);
        client.Verify(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Labels_UnrelatedLossIsUnknown()
    {
        var before = Issue();
        var client = Client(before);
        client.Setup(c => c.ChangeLabelAsync(Account, "octo/tool", 42, "help wanted", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["help wanted"]);
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var result = await session.ExecuteAsync(Account, "octo/tool", before, new(IssueChangeKind.Label, "help wanted"), () => true, TestContext.CancellationToken);
        Assert.AreEqual(MutationState.Unknown, result.State);
        Assert.Contains("unrelated labels", result.Error!);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task InvalidOrRemovedSelection_DoesNotSubmit(bool assignee)
    {
        var client = Client(Issue());
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var result = await session.ExecuteAsync(Account, "octo/tool", Issue(),
            new(assignee ? IssueChangeKind.Assignee : IssueChangeKind.Label, "missing"), () => true, TestContext.CancellationToken);
        Assert.AreEqual(MutationState.Failed, result.State);
        Assert.Contains("no longer available", result.Error!);
        client.Verify(c => c.ChangeAssigneeAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.ChangeLabelAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeniedWrite_ReturnsFailureAndSsoLink()
    {
        var client = Client(Issue());
        var authorize = new Uri("https://github.com/orgs/octo/sso");
        client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Authorize access.", authorizeUrl: authorize));
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var result = await session.ExecuteAsync(Account, "octo/tool", Issue(), new(IssueChangeKind.State, State: SubjectState.Closed), () => true, TestContext.CancellationToken);
        Assert.AreEqual(MutationState.Failed, result.State);
        Assert.AreEqual(authorize, result.AuthorizeUrl);
        Assert.AreEqual("Authorize access.", result.Error);
    }

    [TestMethod]
    public async Task VerificationReadFailure_DoesNotAllowBlindRetry()
    {
        var client = Client(Issue());
        client.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issue()).ThrowsAsync(new GitHubApiException("Read denied.")).ReturnsAsync(Issue());
        client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issue(SubjectState.Closed));
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var change = new IssueChange(IssueChangeKind.State, State: SubjectState.Closed);

        Assert.AreEqual(MutationState.Unknown, (await session.ExecuteAsync(Account, "octo/tool", Issue(), change, () => true, TestContext.CancellationToken)).State);
        Assert.AreEqual(MutationState.Unknown, (await session.ExecuteAsync(Account, "octo/tool", Issue(), change, () => true, TestContext.CancellationToken)).State);
        client.Verify(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UncertainWrite_ReconcilesWithoutResubmitting()
    {
        var client = Client(Issue());
        client.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issue()).ReturnsAsync(Issue(SubjectState.Closed));
        client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Response lost.", outcomeUnknown: true));
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var change = new IssueChange(IssueChangeKind.State, State: SubjectState.Closed);

        Assert.AreEqual(MutationState.Unknown, (await session.ExecuteAsync(Account, "octo/tool", Issue(), change, () => true, TestContext.CancellationToken)).State);
        var reconciled = await session.ExecuteAsync(Account, "octo/tool", Issue(), change, () => true, TestContext.CancellationToken);
        Assert.AreEqual(MutationState.Completed, reconciled.State);
        Assert.AreEqual(SubjectState.Closed, reconciled.Value!.State);
        client.Verify(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [Timeout(10000)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AccountChange_CancelsAndSuppressesOldOperation(bool duringWrite)
    {
        var client = Client(Issue());
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (duringWrite)
        {
            client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
                .Returns((GitHubAccount _, string _, int _, SubjectState _, CancellationToken token) =>
                {
                    started.SetResult(token);
                    return finish.Task;
                });
        }
        else
        {
            client.Setup(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
                .Returns((GitHubAccount _, string _, int _, CancellationToken token) =>
                {
                    started.SetResult(token);
                    return finish.Task;
                });
        }

        using var auth = CreateAuth();
        using var session = new IssueMutationSession(auth, client.Object);
        var task = session.ExecuteAsync(Account, "octo/tool", Issue(), new(IssueChangeKind.State, State: SubjectState.Closed), () => true, TestContext.CancellationToken);
        var token = await started.Task.WaitAsync(TestContext.CancellationToken);
        await auth.SignInWithTokenAsync("https://github.com", "replacement", TestContext.CancellationToken);
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual(MutationState.Stale, (await task).State);
        finish.SetResult(Issue(SubjectState.Closed));
        client.Verify(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()), duringWrite ? Times.Once() : Times.Never());
    }

    [TestMethod]
    [Timeout(10000)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Cancellation_DistinguishesBeforeAndAfterSubmission(bool afterSubmit)
    {
        var client = Client(Issue());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.SetResult();
                return finish.Task;
            });
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        if (!afterSubmit)
        {
            cancellation.Cancel();
        }

        var task = session.ExecuteAsync(Account, "octo/tool", Issue(), new(IssueChangeKind.State, State: SubjectState.Closed), () => true, cancellation.Token);
        if (afterSubmit)
        {
            await started.Task.WaitAsync(TestContext.CancellationToken);
            cancellation.Cancel();
        }

        Assert.AreEqual(afterSubmit ? MutationState.Unknown : MutationState.Failed, (await task).State);
        finish.SetResult(Issue(SubjectState.Closed));
        client.Verify(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()), afterSubmit ? Times.Once() : Times.Never());
    }

    [TestMethod]
    public async Task Picker_LoadsAllPagesAndRejectsCycles()
    {
        var client = Client(Issue());
        var next = new Uri("https://api.github.com/repos/octo/tool/labels?page=2");
        client.Setup(c => c.GetLabelsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueChoicesResult(["bug"], next));
        client.Setup(c => c.GetLabelsAsync(Account, "octo/tool", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueChoicesResult(["help wanted", "BUG"], null));
        using var session = new IssueMutationSession(CreateAuth(), client.Object);

        CollectionAssert.AreEquivalent(ExpectedPagedLabels,
            (await session.ChoicesAsync(Account, "octo/tool", IssueChangeKind.Label, TestContext.CancellationToken)).ToArray());
        client.Setup(c => c.GetLabelsAsync(Account, "octo/tool", next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueChoicesResult(["help wanted"], next));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            session.ChoicesAsync(Account, "octo/tool", IssueChangeKind.Label, TestContext.CancellationToken));
    }

    internal static Mock<IIssueMutationsClient> Client(GitHubIssue issue)
    {
        var client = new Mock<IIssueMutationsClient>();
        client.Setup(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        client.Setup(c => c.GetAssigneesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueChoicesResult(["octocat", "mona"], null));
        client.Setup(c => c.GetLabelsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueChoicesResult(["bug", "help wanted"], null));
        return client;
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task DuplicateConfirmation_SharesOneWrite()
    {
        var client = Client(Issue());
        var updated = Issue(SubjectState.Closed);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.TrySetResult();
                return finish.Task;
            });
        client.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issue()).ReturnsAsync(updated);
        using var session = new IssueMutationSession(CreateAuth(), client.Object);
        var change = new IssueChange(IssueChangeKind.State, State: SubjectState.Closed);
        var first = session.ExecuteAsync(Account, "octo/tool", Issue(), change, () => true, TestContext.CancellationToken);
        await started.Task.WaitAsync(TestContext.CancellationToken);
        var second = session.ExecuteAsync(Account, "octo/tool", Issue(), change, () => true, TestContext.CancellationToken);
        finish.SetResult(updated);

        Assert.AreEqual(MutationState.Completed, (await first).State);
        Assert.AreEqual(MutationState.Completed, (await second).State);
        client.Verify(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()), Times.Once);
    }
}
