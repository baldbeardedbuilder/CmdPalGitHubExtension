using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Agents;

[TestClass]
public sealed class AgentBrowsingTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly GitHubAgentTask TaskItem = new("task-1", "Fix tests", new Uri("https://github.com/copilot/tasks/task-1"),
        "failed", new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), null, "o/r");
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task AgentQuery_SendsArchivedStateAndRepositoryScope()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("/agents/repos/o/r/tasks", request.RequestUri!.AbsolutePath);
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            Assert.AreEqual("true", query["is_archived"]);
            Assert.AreEqual("failed,timed_out", query["state"]);
            Assert.AreEqual("2026-03-10", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            return Json("""{"tasks":[]}""");
        }));
        var result = await new AgentsClient(http).GetTasksAsync(Account, new(true, "failed,timed_out", "o/r"), null, TestContext.CancellationToken);
        Assert.IsEmpty(result.Tasks);
    }

    [TestMethod]
    public async Task AgentPagination_RejectsSwitchingArchivedScope()
    {
        using var http = new HttpClient(new Handler(_ => throw new AssertFailedException("Request should not be sent.")));
        var uri = new Uri("https://api.github.com/agents/tasks?per_page=30&sort=updated_at&direction=desc&is_archived=false&page=2");
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new AgentsClient(http).GetTasksAsync(Account, new(true), uri, TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, "isn't available")]
    [DataRow(HttpStatusCode.Forbidden, "Agent tasks: read")]
    public async Task AgentPreviewErrors_PreserveAvailabilityGuidance(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(new Handler(_ => new(status)));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new AgentsClient(http).GetTasksAsync(Account, new(true), null, TestContext.CancellationToken));
        Assert.Contains(expected, error.Message);
    }

    [TestMethod]
    [DataRow("ai_credits")]
    [DataRow("premium_requests")]
    [DataRow("future_unit")]
    public void AgentDetails_RetainsPromptErrorUsageAndArtifacts(string unit)
    {
        using var json = JsonDocument.Parse($$$"""
            {"sessions":[{"id":"session-1","name":"Implement","state":"failed","created_at":"2026-10-03T12:00:00Z",
            "updated_at":"2026-10-03T12:01:00Z","prompt":"Fix a failing test","model":"model-1","head_ref":"work","base_ref":"main",
            "error":{"message":"Policy denied"},"usage":{"type":"{{{unit}}}","amount":1.25}}],
            "artifacts":[{"provider":"github","type":"branch","data":{"head_ref":"work","base_ref":"main"}},
            {"provider":"github","type":"pull","data":{"id":123,"global_id":"PR_node"}}]}
            """);
        var details = AgentsClient.WithDetails(TaskItem, json.RootElement);
        var session = details.Sessions!.Single();
        Assert.AreEqual("Fix a failing test", session.Prompt);
        Assert.AreEqual("Policy denied", session.Error);
        Assert.AreEqual(unit, session.UsageType);
        Assert.AreEqual(1.25, session.UsageAmount);
        Assert.HasCount(2, details.Artifacts!);
        Assert.AreEqual("work", details.Artifacts![0].HeadRef);
        Assert.AreEqual(123L, details.Artifacts[1].Id);
        using var page = new AgentDetailsPage(null, details, new FakeBrowser(_ => null), Account, () => true);
        var item = page.GetItems().Single(i => i.Title == "Implement");
        Assert.Contains(unit, item.Subtitle);
        Assert.DoesNotContain("$", item.Subtitle);
        Assert.Contains("Policy denied", item.Details!.Body);
    }

    [TestMethod]
    public void OptionalDetails_AbsentSessionsAndUnknownArtifactsRemainUsable()
    {
        using var json = JsonDocument.Parse("""{"artifacts":[{"provider":"future","type":"other","data":{}}]}""");
        var details = AgentsClient.WithDetails(TaskItem, json.RootElement);
        Assert.IsEmpty(details.Sessions!);
        using var page = new AgentDetailsPage(null, details, new FakeBrowser(_ => null), Account, () => true);
        Assert.Contains("no supported navigation", page.GetItems().Single(i => i.Title.Contains("future")).Subtitle);
    }

    [TestMethod]
    public async Task PullArtifact_ResolvesGlobalNodeWithoutTreatingDatabaseIdAsPrNumber()
    {
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/graphql")
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                return Json("""{"data":{"node":{"url":"https://github.com/o/r/pull/7"}}}""");
            }

            return Json("""{"sessions":[],"artifacts":[{"provider":"github","type":"pull","data":{"id":999,"global_id":"PR_node"}}]}""");
        }));
        var task = await new AgentsClient(http).GetTaskAsync(Account, TaskItem, TestContext.CancellationToken);
        Assert.AreEqual(new Uri("https://github.com/o/r/pull/7"), task.Artifacts!.Single().WebUrl);
    }

    [TestMethod]
    public async Task QueryChange_ReplacesOldRowsAndResetsPagination()
    {
        var client = new Mock<IAgentsClient>();
        var browsing = client.As<IAgentBrowsingClient>();
        browsing.Setup(c => c.GetTasksAsync(Account, new AgentQuery(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTasksPageResult([TaskItem], new Uri("https://api.github.com/agents/tasks?page=2")));
        var archived = new AgentQuery(true, "completed");
        browsing.Setup(c => c.GetTasksAsync(Account, archived, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentTasksPageResult([TaskItem with { Id = "archived", State = "completed" }], null));
        using var auth = Auth();
        using var page = new AgentsPage(auth, client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        var old = Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single());
        var detail = Assert.IsInstanceOfType<AgentDetailsPage>(old.MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is AgentDetailsPage).Command);
        await page.SetQuery(archived);
        Assert.AreEqual("archived", Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single()).Task.Id);
        Assert.IsFalse(page.HasMoreItems);
        Assert.IsEmpty(detail.GetItems());
        auth.SignOut();
        Assert.IsEmpty(page.GetItems());
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(callback(request));
    }
}
