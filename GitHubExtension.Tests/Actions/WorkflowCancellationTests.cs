// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Actions;

[TestClass]
public sealed class WorkflowCancellationTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ForceCancel_CannotBypassNormalCancellation()
    {
        var client = Client();
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        await page.ForceCancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        client.Verify(c => c.GetRunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.CancelRunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UnknownRunState_IsNotTreatedAsStopped()
    {
        var client = Client();
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run() with { Status = "future_state" });
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        await page.CancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        client.Verify(c => c.CancelRunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsTrue(page.GetItems().Any(item => item.Subtitle.Contains("unknown workflow state", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task UnknownNormalRequest_IsNotResubmittedAfterRefresh()
    {
        var client = Client();
        client.SetupSequence(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run())
            .ReturnsAsync(Run() with { Status = "completed", Conclusion = "cancelled" });
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("The request may have been accepted.", outcomeUnknown: true));
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        await page.CancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        Assert.IsTrue(page.GetItems().Any(item => item.Subtitle.Contains("may have been accepted", StringComparison.Ordinal)));
        await page.RefreshAsync();
        var item = Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single());
        await page.CancelAsync(item);
        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()), Times.Once);
        page.Filters!.CurrentFilterId = ActionFilters.Failed;
        Assert.AreEqual("completed", Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Status);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Conflict)]
    public async Task DeniedOrConflictingRequest_ShowsErrorAndDoesNotOfferForce(HttpStatusCode status)
    {
        var client = Client();
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>())).ReturnsAsync(Run());
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException($"GitHub returned {(int)status}."));
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        await page.CancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        Assert.IsTrue(page.GetItems().Any(item => item.Subtitle.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)));
        Assert.IsFalse(page.CanForceCancel(1));
    }

    [TestMethod]
    public async Task RepositoryPermissionDenied_DoesNotPost()
    {
        var client = Client();
        var permissions = client.As<IWorkflowCancellationPermissionsClient>();
        permissions.Setup(c => c.CanCancelAsync(Account, "o/r", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>())).ReturnsAsync(Run());
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        await page.CancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        client.Verify(c => c.CancelRunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsTrue(page.GetItems().Any(item => item.Subtitle.Contains("write access", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("""{"permissions":{"push":true}}""", true)]
    [DataRow("""{"permissions":{"push":false,"admin":false,"maintain":false}}""", false)]
    [DataRow("""{"permissions":{"maintain":true}}""", true)]
    public async Task Client_ReadsRepositoryPermission(string json, bool expected)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("https://api.github.com/repos/o/r", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        Assert.AreEqual(expected, await new ActionsClient(http).CanCancelAsync(Account, "o/r", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Client_UnexpectedSuccessIsUnknownNotConfirmedCancellation()
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new ActionsClient(http).CancelRunAsync(Account, "o/r", 1, false, TestContext.CancellationToken));
        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task AcceptedThenAccountChanges_DiscardsOldResult()
    {
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client();
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>())).ReturnsAsync(Run());
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                submitted.SetResult();
                return response.Task;
            });
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        var cancellation = page.CancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        await submitted.Task.WaitAsync(TestContext.CancellationToken);
        auth.SignOut();
        response.SetResult();
        await cancellation;
        Assert.IsEmpty(page.GetItems());
        Assert.IsFalse(page.CanForceCancel(1));
        client.Verify(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Refresh_StopsPollingEvenWhenReadIgnoresCancellation()
    {
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<GitHubWorkflowRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var client = Client();
        client.Setup(c => c.GetRunAsync(Account, "o/r", 1, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref reads) < 3)
                {
                    return Task.FromResult(Run());
                }

                polling.SetResult();
                return late.Task;
            });
        client.Setup(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var auth = Auth();
        using var page = await Loaded(client.Object, auth);
        var cancellation = page.CancelAsync(Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()));
        await polling.Task.WaitAsync(TestContext.CancellationToken);
        await page.RefreshAsync();
        await cancellation.WaitAsync(TestContext.CancellationToken);
        late.SetResult(Run() with { Status = "completed", Conclusion = "cancelled" });
        Assert.AreEqual("in_progress", Assert.IsInstanceOfType<WorkflowRunItem>(page.GetItems().Single()).Run.Status);
        client.Verify(c => c.CancelRunAsync(Account, "o/r", 1, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static GitHubWorkflowRun Run() => new(1, "CI", "CI", "mona", "in_progress", null, DateTimeOffset.UtcNow,
        new Uri("https://github.com/o/r/actions/runs/1"));

    private static Mock<IActionsClient> Client()
    {
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([Run()], null));
        return client;
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static async Task<ActionsPage> Loaded(IActionsClient client, AuthService auth)
    {
        var page = new ActionsPage(auth, client, new FakeBrowser(_ => null));
        page.OpenRepository("o/r");
        page.GetItems();
        await page.CurrentLoad;
        return page;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
