// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Actions;

[TestClass]
public class WorkflowRerunTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly GitHubWorkflowRun Run = new(9876543210, "CI", "Test", "octocat", "completed", "failure",
        DateTimeOffset.UtcNow, new Uri("https://github.com/o/r/actions/runs/9876543210"), RunAttempt: 2);
    private const string Confirm = """{"action":"rerun"}""";
    private const string Refresh = """{"action":"refresh"}""";
    private const string SingleRunJson = """
        {"id":9876543210,"name":"CI","status":"queued","run_attempt":3,
        "html_url":"https://github.com/o/r/actions/runs/9876543210"}
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, false, "github.com")]
    [DataRow(false, true, "github.com")]
    [DataRow(true, false, "github.com")]
    [DataRow(true, true, "github.example.com")]
    public async Task Client_PostsDebugOptionAndAcceptsEmptyCreatedResponse(bool failedOnly, bool debug, string hostName)
    {
        Assert.IsTrue(GitHubHost.TryParse(hostName, out var host));
        var account = Account with { Host = host! };
        var handler = new Handler(HttpStatusCode.Created, string.Empty);
        using var http = new HttpClient(handler);

        await new ActionsClient(http).RerunAsync(account, "o/r", Run.Id, failedOnly, debug, TestContext.CancellationToken);

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual(new Uri(host!.ApiUrl, $"repos/o/r/actions/runs/9876543210/{(failedOnly ? "rerun-failed-jobs" : "rerun")}"), handler.Uri);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual(debug, body.RootElement.GetProperty("enable_debug_logging").GetBoolean());
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.IsTrue(handler.HasAuthorization);
        Assert.AreEqual("2022-11-28", handler.ApiVersion);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.NotFound)]
    [DataRow(HttpStatusCode.UnprocessableEntity)]
    public async Task Client_RejectsPermissionAndStateErrors(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(status, string.Empty));
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            new ActionsClient(http).RerunAsync(Account, "o/r", Run.Id, false, false, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Client_GetRunUsesExistingIdAndParsesAttempt()
    {
        var handler = new Handler(HttpStatusCode.OK, SingleRunJson);
        using var http = new HttpClient(handler);

        var result = await new ActionsClient(http).GetRunAsync(Account, "o/r", Run.Id, TestContext.CancellationToken);

        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual(new Uri("https://api.github.com/repos/o/r/actions/runs/9876543210"), handler.Uri);
        Assert.AreEqual(Run.Id, result.Id);
        Assert.AreEqual(3, result.RunAttempt);
        Assert.AreEqual("queued", result.Status);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("{}")]
    [DataRow("""{"id":1,"html_url":"https://github.com/o/r/actions/runs/1"}""")]
    public async Task Client_GetRunRejectsEmptyMalformedOrDifferentRun(string body)
    {
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, body));
        await Assert.ThrowsAsync<GitHubApiException>(() =>
            new ActionsClient(http).GetRunAsync(Account, "o/r", Run.Id, TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow("in_progress", null, false, false)]
    [DataRow("queued", null, false, false)]
    [DataRow("completed", "success", true, false)]
    [DataRow("completed", "failure", true, true)]
    [DataRow("completed", "timed_out", true, true)]
    [DataRow("completed", "cancelled", true, false)]
    [DataRow("completed", "skipped", true, false)]
    public async Task MenuAndConfirmationOfferStateAppropriateChoices(string status, string? conclusion, bool all, bool failed)
    {
        var run = Run with { Status = status, Conclusion = conclusion };
        var client = Client(run);
        using var parent = await Parent(client.Object, Auth());
        parent.Filters!.CurrentFilterId = status == "completed"
            ? conclusion == "success" ? ActionFilters.Succeeded : ActionFilters.Failed
            : ActionFilters.Running;
        var item = parent.GetItems().Single();
        var page = item.MoreCommands.OfType<CommandContextItem>().Select(c => c.Command).OfType<RerunWorkflowPage>().SingleOrDefault();
        Assert.AreEqual(all, page is not null);
        if (page is not null)
        {
            using var card = JsonDocument.Parse(Template(page));
            StringAssert.Contains(Template(page), "compute");
            StringAssert.Contains(Template(page), "charges");
            StringAssert.Contains(Template(page), "Enable debug logging");
            Assert.AreEqual(failed, Template(page).Contains("Failed jobs and their dependents", StringComparison.Ordinal));
            client.Verify(c => c.RerunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task ConfirmationRefreshesSameRunWithoutPrematureSuccess(bool failedOnly, bool debug)
    {
        var auth = Auth();
        var client = Client(Run);
        var queued = Run with { RunAttempt = 3, Status = "queued", Conclusion = null };
        client.SetupSequence(c => c.GetRunAsync(Account, "o/r", Run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run).ReturnsAsync(queued).ReturnsAsync(queued with { Status = "completed", Conclusion = "success" });
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([queued], null));
        using var parent = await Parent(client.Object, auth);
        var page = parent.RerunPage("o/r", Run);

        page.HandleSubmit($$"""{"jobs":"{{(failedOnly ? "failed" : "all")}}","debug":"{{debug.ToString().ToLowerInvariant()}}"}""", Confirm);
        await page.CurrentOperation;

        client.Verify(c => c.RerunAsync(Account, "o/r", Run.Id, failedOnly, debug, It.IsAny<CancellationToken>()), Times.Once);
        StringAssert.Contains(Template(page), "Rerun requested. Attempt 3: Queued.");
        Assert.IsFalse(Template(page).Contains("Success", StringComparison.Ordinal));
        parent.Filters!.CurrentFilterId = ActionFilters.Running;
        var item = (WorkflowRunItem)parent.GetItems().Single();
        Assert.AreEqual(Run.Id, item.Run.Id);
        Assert.AreEqual(3, item.Run.RunAttempt);

        page.HandleSubmit("{}", Refresh);
        await page.CurrentOperation;
        StringAssert.Contains(Template(page), "Attempt 3: Success.");
        client.Verify(c => c.RerunAsync(Account, "o/r", Run.Id, failedOnly, debug, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AcceptedRequestWithDelayedAttemptDoesNotShowOldSuccess()
    {
        var run = Run with { Conclusion = "success" };
        var client = Client(run);
        using var parent = await Parent(client.Object, Auth());
        var page = parent.RerunPage("o/r", run);
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;

        StringAssert.Contains(Template(page), "Waiting for GitHub to report the new attempt");
        Assert.IsFalse(Template(page).Contains("Attempt 2: Success", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("queued", "failure", 2)]
    [DataRow("completed", "failure", 3)]
    [DataRow("completed", "success", 2)]
    public async Task RechecksStateAndAttemptBeforePosting(string status, string conclusion, int attempt)
    {
        var client = Client(Run);
        client.Setup(c => c.GetRunAsync(Account, "o/r", Run.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Run with { Status = status, Conclusion = conclusion, RunAttempt = attempt });
        using var parent = await Parent(client.Object, Auth());
        var page = parent.RerunPage("o/r", Run);
        page.HandleSubmit("""{"jobs":"failed"}""", Confirm);
        await page.CurrentOperation;

        client.Verify(c => c.RerunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        StringAssert.Contains(Template(page), "state or attempt changed");
    }

    [TestMethod]
    public async Task DuplicateActivationAndListRefreshShareSingleSubmission()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(Run);
        client.Setup(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()))
            .Returns(() => { entered.SetResult(); return gate.Task; });
        using var parent = await Parent(client.Object, Auth());
        var page = parent.RerunPage("o/r", Run);
        page.HandleSubmit("{}", Confirm);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        page.HandleSubmit("{}", Confirm);
        await parent.RefreshAsync();
        Assert.AreSame(page, parent.RerunPage("o/r", Run));
        gate.SetResult();
        await page.CurrentOperation;
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;

        client.Verify(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccountChangeCancelsAndRejectsStaleConfirmation(bool duringPost)
    {
        var auth = Auth();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(Run);
        CancellationToken operationToken = default;
        if (duringPost)
        {
            client.Setup(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()))
                .Returns((GitHubAccount _, string _, long _, bool _, bool _, CancellationToken token) =>
                { operationToken = token; entered.SetResult(); return gate.Task; });
        }
        else
        {
            client.Setup(c => c.GetRunAsync(Account, "o/r", Run.Id, It.IsAny<CancellationToken>()))
                .Returns(async (GitHubAccount _, string _, long _, CancellationToken token) =>
                { operationToken = token; entered.SetResult(); await gate.Task; return Run; });
        }

        using var parent = await Parent(client.Object, auth);
        var page = parent.RerunPage("o/r", Run);
        page.HandleSubmit("{}", Confirm);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        auth.SignOut();
        gate.SetResult();
        await page.CurrentOperation;
        page.HandleSubmit("{}", Confirm);
        page.HandleSubmit("{}", Refresh);
        await page.CurrentOperation;

        Assert.IsTrue(operationToken.IsCancellationRequested);
        StringAssert.Contains(Template(page), "Account changed");
        Assert.IsEmpty(parent.GetItems());
        client.Verify(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()), duringPost ? Times.Once() : Times.Never());
        client.Verify(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RequestAndRefreshErrorsRemainVisibleAndDoNotAllowDuplicatePost(bool refreshError)
    {
        var client = Client(Run);
        if (refreshError)
        {
            client.SetupSequence(c => c.GetRunAsync(Account, "o/r", Run.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Run).ThrowsAsync(new GitHubApiException("Unavailable"));
        }
        else
        {
            client.Setup(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GitHubApiException("GitHub said no. Your token might be missing a scope."));
        }

        using var parent = await Parent(client.Object, Auth());
        var page = parent.RerunPage("o/r", Run);
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;
        using var card = JsonDocument.Parse(Template(page));
        var message = card.RootElement.GetProperty("body")[1].GetProperty("text").GetString()!;
        StringAssert.Contains(message, refreshError ? "status couldn't be refreshed" : "missing a scope");
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;
        client.Verify(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task RefreshAfterPreflightFailureRestoresConfirmationWithoutDuplicatePost()
    {
        var client = Client(Run);
        client.SetupSequence(c => c.GetRunAsync(Account, "o/r", Run.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Unavailable"))
            .ReturnsAsync(Run).ReturnsAsync(Run).ReturnsAsync(Run with { Status = "queued", RunAttempt = 3 });
        using var parent = await Parent(client.Object, Auth());
        var page = parent.RerunPage("o/r", Run);
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;
        StringAssert.Contains(Template(page), "Unavailable");
        client.Verify(c => c.RerunAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);

        page.HandleSubmit("{}", Refresh);
        await page.CurrentOperation;
        StringAssert.Contains(Template(page), "Confirm rerun");
        Assert.AreSame(page, parent.RerunPage("o/r", Run));
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;
        client.Verify(c => c.RerunAsync(Account, "o/r", Run.Id, false, false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task NotificationsAllowHostToReadContentFromAnotherThread()
    {
        using var parent = await Parent(Client(Run).Object, Auth());
        var page = parent.RerunPage("o/r", Run);
        var notifications = 0;
        page.ItemsChanged += (_, _) =>
        {
            var content = Task.Run(page.GetContent).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Assert.HasCount(1, content);
            Interlocked.Increment(ref notifications);
        };
        page.HandleSubmit("{}", Confirm);
        await page.CurrentOperation;
        page.Dispose();
        Assert.IsGreaterThan(0, notifications);
    }

    private static string Template(RerunWorkflowPage page) => ((FormContent)page.GetContent().Single()).TemplateJson;

    private static AuthService Auth() =>
        new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static Mock<IActionsClient> Client(GitHubWorkflowRun run)
    {
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetRunsAsync(Account, "o/r", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunsPageResult([run], null));
        client.Setup(c => c.GetRunAsync(Account, "o/r", run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(run);
        client.Setup(c => c.RerunAsync(Account, "o/r", run.Id, It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return client;
    }

    private static async Task<ActionsPage> Parent(IActionsClient client, AuthService auth)
    {
        var parent = new ActionsPage(auth, client, new FakeBrowser(_ => null));
        parent.OpenRepository("o/r");
        parent.GetItems();
        await parent.CurrentLoad;
        return parent;
    }

    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public bool HasAuthorization { get; private set; }
        public string? ApiVersion { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            HasAuthorization = request.Headers.Authorization is not null;
            ApiVersion = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            if (request.Content is not null)
            {
                ContentType = request.Content.Headers.ContentType?.MediaType;
                Body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
