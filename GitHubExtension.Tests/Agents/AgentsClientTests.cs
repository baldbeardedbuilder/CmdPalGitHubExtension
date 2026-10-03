// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Agents;

[TestClass]
public sealed class AgentsClientTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly string[] CreateOnly = ["POST"];
    private static readonly string[] CreateTextFields = ["prompt", "model", "custom_agent", "base_ref", "head_ref"];
    private const string TaskJson = """
        {
          "id": "task-1",
          "name": "Fix token expiry",
          "html_url": "https://github.com/copilot/tasks/task-1",
          "state": "in_progress",
          "repository": { "id": 4000000000 },
          "created_at": "2026-10-01T11:00:00Z",
          "updated_at": "2026-10-02T11:48:00Z"
        }
        """;
    private const string DetailsJson = """
        { "sessions": [
            { "created_at": "2026-10-02T11:00:00Z", "model": "claude-sonnet-5" },
            { "created_at": "2026-10-01T11:00:00Z", "model": "older-model" }
        ] }
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetTasksAsync_ListsAuthenticatedUserTasksAndEnrichesSubtitles()
    {
        using var handler = Handler();
        using var http = new HttpClient(handler);
        var result = await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken);

        var task = result.Tasks.Single();
        Assert.AreEqual("task-1", task.Id);
        Assert.AreEqual("Fix token expiry", task.Title);
        Assert.AreEqual("in_progress", task.State);
        Assert.AreEqual(new Uri("https://github.com/copilot/tasks/task-1"), task.WebUrl);
        Assert.AreEqual("microsoft/PowerToys", task.RepositoryFullName);
        Assert.AreEqual("claude-sonnet-5", task.Model);
        Assert.AreEqual(4000000000L, task.RepositoryId);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 2, 11, 48, 0, TimeSpan.Zero), task.UpdatedAt);
        Assert.IsNull(task.DetailsError);
        Assert.IsNull(result.NextPage);
        Assert.Contains("/agents/tasks?per_page=30&sort=updated_at&direction=desc&is_archived=false", handler.Paths);
        Assert.Contains("/agents/tasks/task-1", handler.Paths);
        Assert.Contains("/repositories/4000000000", handler.Paths);
        Assert.IsTrue(handler.Headers.All(h => h.Token == "test-token" && h.Accept == "application/vnd.github+json" && h.HasUserAgent));
        Assert.IsTrue(handler.Headers.Where(h => h.Path.StartsWith("/agents/", StringComparison.Ordinal)).All(h => h.Version == "2026-03-10"));
        Assert.AreEqual("2022-11-28", handler.Headers.Single(h => h.Path.StartsWith("/repositories/", StringComparison.Ordinal)).Version);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\"html_url\": null,")]
    public async Task GetItems_MissingTaskUrlDisplaysAgentWithWorkingLink(string urlField)
    {
        var payload = TaskJson.Replace("\"html_url\": \"https://github.com/copilot/tasks/task-1\",", urlField, StringComparison.Ordinal);
        using var handler = Handler(tasks: $"{{\"tasks\":[{payload}]}}");
        using var http = new HttpClient(handler);

        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var browser = new FakeBrowser(_ => null);
        using var page = new AgentsPage(auth, new AgentsClient(http), browser);

        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<AgentItem>(page.GetItems().Single());
        var task = item.Task;

        Assert.AreEqual("task-1", task.Id);
        Assert.AreEqual("Fix token expiry", item.Title);
        Assert.AreEqual(new Uri("https://github.com/copilot/tasks/task-1"), task.WebUrl);
        Assert.AreEqual("microsoft/PowerToys", task.RepositoryFullName);
        Assert.AreEqual("claude-sonnet-5", task.Model);
        Assert.IsNull(task.DetailsError);
        Assert.IsFalse(page.IsLoading);
        Assert.IsInstanceOfType<OpenInBrowserCommand>(item.Command).Invoke();
        Assert.AreEqual(task.WebUrl, browser.LastOpened);
    }

    [TestMethod]
    public async Task GetTasksAsync_UsesNextLinkAndCachesRepositoryWithinPage()
    {
        var next = new Uri("https://api.github.com/agents/tasks?per_page=30&sort=updated_at&direction=desc&is_archived=false&page=2");
        using var handler = Handler(tasks: $"{{\"tasks\":[{TaskJson},{TaskJson.Replace("task-1", "task-2", StringComparison.Ordinal)}]}}", next: next);
        using var http = new HttpClient(handler);

        var result = await new AgentsClient(http).GetTasksAsync(Account, next, TestContext.CancellationToken);

        Assert.AreEqual(next.PathAndQuery, handler.Paths.First());
        Assert.AreEqual(next, result.NextPage);
        Assert.HasCount(2, result.Tasks);
        Assert.AreEqual(1, handler.Paths.Count(p => p == "/repositories/4000000000"));
    }

    [TestMethod]
    public async Task GetTasksAsync_EmptyListDoesNotFetchDetails()
    {
        using var handler = Handler(tasks: """{"tasks":[]}""");
        using var http = new HttpClient(handler);

        var result = await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken);

        Assert.IsEmpty(result.Tasks);
        Assert.HasCount(1, handler.Paths);
    }

    [TestMethod]
    public async Task GetTasksAsync_ModelFailureKeepsTaskAndShowsError()
    {
        using var handler = Handler(detailsStatus: HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler);

        var task = (await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken)).Tasks.Single();

        Assert.AreEqual("Fix token expiry", task.Title);
        Assert.AreEqual("microsoft/PowerToys", task.RepositoryFullName);
        Assert.IsNull(task.Model);
        Assert.Contains("Couldn't load the model.", task.DetailsError!);
        Assert.Contains("Agent tasks: read", task.DetailsError!);
    }

    [TestMethod]
    public async Task GetTasksAsync_RepositoryFailureKeepsTaskAndShowsError()
    {
        using var handler = Handler(repositoryStatus: HttpStatusCode.NotFound);
        using var http = new HttpClient(handler);

        var task = (await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken)).Tasks.Single();

        Assert.IsNull(task.RepositoryFullName);
        Assert.AreEqual("claude-sonnet-5", task.Model);
        Assert.Contains("Couldn't load the repository.", task.DetailsError!);
        Assert.Contains("404", task.DetailsError!);
    }

    [TestMethod]
    public async Task GetTasksAsync_InvalidRepositoryKeepsTaskAndShowsError()
    {
        var payload = TaskJson.Replace("\"id\": 4000000000", "\"id\": \"bad\"", StringComparison.Ordinal);
        using var handler = Handler(tasks: $"{{\"tasks\":[{payload}]}}");
        using var http = new HttpClient(handler);

        var task = (await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken)).Tasks.Single();

        Assert.AreEqual("Fix token expiry", task.Title);
        Assert.IsNull(task.RepositoryId);
        Assert.IsNull(task.RepositoryFullName);
        Assert.AreEqual("claude-sonnet-5", task.Model);
        Assert.Contains("Couldn't load the repository.", task.DetailsError!);
        Assert.Contains("invalid repository", task.DetailsError!);
        Assert.AreEqual(
            "Repository unavailable · claude-sonnet-5 · 12h ago · Couldn't load the repository. GitHub sent back an agent task with an invalid repository.",
            AgentFormatting.Subtitle(task, new DateTimeOffset(2026, 10, 2, 23, 48, 0, TimeSpan.Zero)));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "Sign out")]
    [DataRow(HttpStatusCode.Forbidden, "Agent tasks: read")]
    [DataRow(HttpStatusCode.NotFound, "isn't available")]
    [DataRow(HttpStatusCode.TooManyRequests, "rate limit")]
    [DataRow(HttpStatusCode.InternalServerError, "500")]
    public async Task GetTasksAsync_ApiErrorsAreActionable(HttpStatusCode status, string message)
    {
        using var handler = Handler(listStatus: status);
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(
            () => new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken));

        Assert.Contains(message, error.Message);
        Assert.HasCount(1, handler.Paths);
    }

    [TestMethod]
    [DataRow("https://evil.example/agents/tasks")]
    [DataRow("http://api.github.com/agents/tasks")]
    public async Task GetTasksAsync_RejectsUnsafePaginationWithoutSendingToken(string url)
    {
        using var handler = Handler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(
            () => new AgentsClient(http).GetTasksAsync(Account, new Uri(url), TestContext.CancellationToken));

        Assert.IsEmpty(handler.Paths);
    }

    [TestMethod]
    public async Task GetTasksAsync_CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        using var handler = Handler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => new AgentsClient(http).GetTasksAsync(Account, null, cancellation.Token));
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task GetTasksAsync_ThrottlesDetailsRequests()
    {
        using var handler = new ThrottledHandler();
        using var http = new HttpClient(handler);
        var load = new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken);
        await handler.SixStarted.Task.WaitAsync(TestContext.CancellationToken);
        Assert.AreEqual(6, handler.Started);

        handler.Release.SetResult();
        var result = await load;

        Assert.HasCount(12, result.Tasks);
        Assert.AreEqual(12, handler.Started);
        Assert.AreEqual(6, handler.MaximumActive);
        Assert.IsTrue(result.Tasks.All(t => t.Model == "claude-sonnet-5" && t.DetailsError is null));
    }

    [TestMethod]
    public async Task GetTasksAsync_EnrichmentTimeoutKeepsTaskWithError()
    {
        using var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.StartsWith("/agents/tasks/", StringComparison.Ordinal)
                ? throw new TaskCanceledException("request timed out")
                : request.RequestUri.AbsolutePath.StartsWith("/repositories/", StringComparison.Ordinal)
                    ? Response(HttpStatusCode.OK, """{"full_name":"microsoft/PowerToys"}""")
                    : Response(HttpStatusCode.OK, $"{{\"tasks\":[{TaskJson}]}}"));
        using var http = new HttpClient(handler);

        var task = (await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken)).Tasks.Single();

        Assert.AreEqual("Fix token expiry", task.Title);
        Assert.AreEqual("microsoft/PowerToys", task.RepositoryFullName);
        Assert.Contains("Couldn't load the model. GitHub took too long to respond. Try refreshing agents.", task.DetailsError!);
    }

    [TestMethod]
    public async Task StartTaskAsync_ListsForTheRepositoryAndPostsOptionalChoices()
    {
        var requests = new List<(string Method, string Path, string Body, string Version)>();
        using var handler = new RequestHandler(async request =>
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(TestContext.CancellationToken);
            var version = request.Headers.GetValues("X-GitHub-Api-Version").Single();
            requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body, version));
            return request.Method == HttpMethod.Get
                ? Response(HttpStatusCode.OK, """{"tasks":[]}""")
                : Response(HttpStatusCode.Created, """
                    {"id":"created-1","name":"Add tests","state":"queued","html_url":"https://github.com/copilot/tasks/created-1"}
                    """);
        });
        using var http = new HttpClient(handler);
        var client = new AgentsClient(http);

        Assert.IsEmpty(await client.GetRepositoryTasksAsync(Account, "octocat/hello", TestContext.CancellationToken));
        var task = await client.StartTaskAsync(Account, "octocat/hello",
            new AgentTaskRequest(" Add tests ", "custom-model", "test-writer", "main", "feature/tests", true),
            TestContext.CancellationToken);

        Assert.AreEqual("created-1", task.Id);
        Assert.AreEqual("GET", requests[0].Method);
        Assert.Contains("/agents/repos/octocat/hello/tasks?per_page=100&sort=created_at&direction=desc", requests[0].Path);
        Assert.AreEqual("POST", requests[1].Method);
        Assert.AreEqual("/agents/repos/octocat/hello/tasks", requests[1].Path);
        Assert.AreEqual("2026-03-10", requests[0].Version);
        Assert.AreEqual("2026-03-10", requests[1].Version);
        using var payload = JsonDocument.Parse(requests[1].Body);
        Assert.AreEqual("Add tests", payload.RootElement.GetProperty("prompt").GetString());
        Assert.AreEqual("custom-model", payload.RootElement.GetProperty("model").GetString());
        Assert.AreEqual("test-writer", payload.RootElement.GetProperty("custom_agent").GetString());
        Assert.AreEqual("main", payload.RootElement.GetProperty("base_ref").GetString());
        Assert.AreEqual("feature/tests", payload.RootElement.GetProperty("head_ref").GetString());
        Assert.IsTrue(payload.RootElement.GetProperty("create_pull_request").GetBoolean());
    }

    [TestMethod]
    public async Task StartTaskAsync_TrimsAndEscapesPromptAndOptionsWithoutDroppingFalse()
    {
        const string value = "quote\"\\line\n\t\u263a";
        using var handler = new RequestHandler(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.CancellationToken));
            var root = json.RootElement;
            foreach (var name in CreateTextFields)
                Assert.AreEqual(value, root.GetProperty(name).GetString());
            Assert.IsFalse(root.GetProperty("create_pull_request").GetBoolean());
            Assert.AreEqual(6, root.EnumerateObject().Count());
            return Response(HttpStatusCode.Created, """{"id":"new","state":"queued"}""");
        });
        using var http = new HttpClient(handler);

        var task = await new AgentsClient(http).StartTaskAsync(Account, "octocat/hello",
            new AgentTaskRequest($" {value} ", $" {value} ", $" {value} ", $" {value} ", $" {value} ", false),
            TestContext.CancellationToken);

        Assert.AreEqual("new", task.Id);
    }

    [TestMethod]
    public async Task StartTaskAsync_OmitsOptionalEmptyValuesAndRejectsInvalidDrafts()
    {
        var requests = new List<string>();
        using var handler = new RequestHandler(async request =>
        {
            requests.Add(request.Method.Method);
            if (request.Method == HttpMethod.Get)
            {
                return Response(HttpStatusCode.OK, """{"tasks":[]}""");
            }

            var body = await request.Content!.ReadAsStringAsync(TestContext.CancellationToken);
            using var json = JsonDocument.Parse(body);
            Assert.IsFalse(json.RootElement.TryGetProperty("model", out _));
            Assert.IsFalse(json.RootElement.TryGetProperty("custom_agent", out _));
            Assert.IsFalse(json.RootElement.TryGetProperty("base_ref", out _));
            Assert.IsFalse(json.RootElement.TryGetProperty("head_ref", out _));
            return Response(HttpStatusCode.Created, """{"id":"new","state":"queued"}""");
        });
        using var http = new HttpClient(handler);
        var client = new AgentsClient(http);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => client.StartTaskAsync(Account, "octocat/hello",
            new AgentTaskRequest(" ", null, null, null, null, false), TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => client.StartTaskAsync(Account, "not-a-repository",
            new AgentTaskRequest("prompt", null, null, null, null, false), TestContext.CancellationToken));
        Assert.IsEmpty(requests);

        var task = await client.StartTaskAsync(Account, "octocat/hello",
            new AgentTaskRequest("prompt", "  ", "", "", null, false), TestContext.CancellationToken);
        Assert.AreEqual("new", task.Id);
        CollectionAssert.AreEqual(CreateOnly, requests);
    }

    [TestMethod]
    public async Task StartTaskAsync_ForbiddenExplainsPlanAndWritePermission()
    {
        using var handler = new RequestHandler(request => Task.FromResult(request.Method == HttpMethod.Get
            ? Response(HttpStatusCode.OK, """{"tasks":[]}""")
            : Response(HttpStatusCode.Forbidden, "{}")));
        using var http = new HttpClient(handler);
        var client = new AgentsClient(http);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.StartTaskAsync(Account, "octocat/hello", new AgentTaskRequest("prompt", null, null, null, null, false),
                TestContext.CancellationToken));

        Assert.Contains("Copilot Business or Enterprise", error.Message);
        Assert.Contains("Agent tasks: read and write", error.Message);
        Assert.IsTrue(OperationDiagnostics.HasFailure(error));
        Assert.AreEqual(DiagnosticFailure.Http, OperationDiagnostics.FailureCategory(error));
    }

    [TestMethod]
    public async Task StartTaskAsync_TimeoutIsMarkedAsAnUnknownOutcome()
    {
        using var handler = new RequestHandler(request => request.Method == HttpMethod.Get
            ? Task.FromResult(Response(HttpStatusCode.OK, """{"tasks":[]}"""))
            : Task.FromException<HttpResponseMessage>(new TaskCanceledException("request timed out")));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<AgentTaskOutcomeUnknownException>(() =>
            new AgentsClient(http).StartTaskAsync(Account, "octocat/hello",
                new AgentTaskRequest("prompt", null, null, null, null, false), TestContext.CancellationToken));

        Assert.Contains("Check the repository's agent tasks", error.Message);
        Assert.IsTrue(OperationDiagnostics.HasFailure(error));
        Assert.AreEqual(DiagnosticFailure.Timeout, OperationDiagnostics.FailureCategory(error));
    }

    [TestMethod]
    public async Task StartTaskAsync_ServerFailurePreservesUnknownDiagnosticCorrelation()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var handler = new RequestHandler(_ => Task.FromResult(Response(HttpStatusCode.InternalServerError, "{}")));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<AgentTaskOutcomeUnknownException>(() =>
            OperationDiagnostics.RunAsync(DiagnosticEvent.Mutation, () =>
                new AgentsClient(http).StartTaskAsync(Account, "octocat/hello",
                    new AgentTaskRequest("prompt", null, null, null, null, false), TestContext.CancellationToken)));

        Assert.IsTrue(OperationDiagnostics.HasFailure(error));
        Assert.AreEqual(DiagnosticFailure.Http, OperationDiagnostics.FailureCategory(error));
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        Assert.HasCount(1, entries.Where(entry => entry.Severity == DiagnosticSeverity.Warning));
        Assert.AreEqual(DiagnosticOutcome.Unknown, entries[^1].Outcome);
        Assert.AreEqual(DiagnosticSeverity.Information, entries[^1].Severity);
    }

    [TestMethod]
    public void ParseTasks_UsesCreatedAtWhenUpdatedAtIsAbsent()
    {
        using var json = JsonDocument.Parse($"{{\"tasks\":[{TaskJson.Replace("\"updated_at\": \"2026-10-02T11:48:00Z\"", "\"updated_at\": null", StringComparison.Ordinal)}]}}");

        var task = AgentsClient.ParseTasks(json.RootElement, Account.Host).Single();

        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 11, 0, 0, TimeSpan.Zero), task.UpdatedAt);
    }

    [TestMethod]
    [DataRow("github.com")]
    [DataRow("octocorp.ghe.com")]
    [DataRow("github.example.com:8443")]
    public void ParseTasks_MissingTaskUrlUsesAccountHostAndEscapesId(string hostname)
    {
        Assert.IsTrue(GitHubHost.TryParse(hostname, out var host));
        using var json = JsonDocument.Parse("""
            {"tasks":[{"id":"task/1?source=#fragment","state":"queued","created_at":"2026-10-01T00:00:00Z"}]}
            """);

        var task = AgentsClient.ParseTasks(json.RootElement, host).Single();

        Assert.AreEqual($"https://{hostname}/copilot/tasks/task%2F1%3Fsource%3D%23fragment", task.WebUrl.AbsoluteUri);
        Assert.AreEqual("queued", task.State);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), task.UpdatedAt);
    }

    [TestMethod]
    public void ParseTasks_SuppliedTaskUrlIsPreserved()
    {
        using var json = JsonDocument.Parse("""
            {"tasks":[{"id":"task-1","state":"completed","html_url":"https://github.com/copilot/tasks/task-1?source=agents","created_at":"2026-10-01T00:00:00Z"}]}
            """);

        var task = AgentsClient.ParseTasks(json.RootElement, Account.Host).Single();

        Assert.AreEqual(new Uri("https://github.com/copilot/tasks/task-1?source=agents"), task.WebUrl);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("""{"tasks":null}""")]
    [DataRow("""{"tasks":[42]}""")]
    [DataRow("""{"tasks":[{"id":"x","state":"completed","html_url":"https://evil.example/tasks/x","created_at":"2026-10-01T00:00:00Z"}]}""")]
    [DataRow("""{"tasks":[{"id":"x","state":"completed","html_url":"http://github.com/tasks/x","created_at":"2026-10-01T00:00:00Z"}]}""")]
    [DataRow("""{"tasks":[{"id":"x","state":"completed","html_url":"https://user@github.com/tasks/x","created_at":"2026-10-01T00:00:00Z"}]}""")]
    [DataRow("""{"tasks":[{"id":"x","state":"completed","html_url":"","created_at":"2026-10-01T00:00:00Z"}]}""")]
    [DataRow("""{"tasks":[{"id":"x","state":"completed","html_url":42,"created_at":"2026-10-01T00:00:00Z"}]}""")]
    [DataRow("""{"tasks":[{"id":"x","state":"completed","html_url":"https://github.com/tasks/x","created_at":"bad"}]}""")]
    public void ParseTasks_InvalidResponsesAreNotEmptySuccesses(string payload)
    {
        using var json = JsonDocument.Parse(payload);

        Assert.ThrowsExactly<GitHubApiException>(() => AgentsClient.ParseTasks(json.RootElement, Account.Host));
    }

    [TestMethod]
    public void ParseModel_UsesNewestSessionEvenWhenItHasNoModel()
    {
        using var json = JsonDocument.Parse("""
            {"sessions":[
              {"created_at":"2026-10-01T00:00:00Z","model":"old"},
              {"created_at":"2026-10-02T00:00:00Z","model":null}
            ]}
            """);

        Assert.IsNull(AgentsClient.ParseModel(json.RootElement));
    }

    [TestMethod]
    public void ParseModel_EmptySessionsHasNoModel()
    {
        using var json = JsonDocument.Parse("""{"sessions":[]}""");

        Assert.IsNull(AgentsClient.ParseModel(json.RootElement));
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""{"sessions":null}""")]
    [DataRow("""{"sessions":[42]}""")]
    public void ParseModel_MalformedDetailsShowAnError(string payload)
    {
        using var json = JsonDocument.Parse(payload);

        Assert.ThrowsExactly<GitHubApiException>(() => AgentsClient.ParseModel(json.RootElement));
    }

    [TestMethod]
    public void ParseTasks_InvalidRepositoryKeepsTaskAndReportsRepositoryError()
    {
        using var json = JsonDocument.Parse($"{{\"tasks\":[{TaskJson.Replace("4000000000", "\"bad\"", StringComparison.Ordinal)}]}}");

        var task = AgentsClient.ParseTasks(json.RootElement, Account.Host).Single();

        Assert.AreEqual("task-1", task.Id);
        Assert.IsNull(task.RepositoryId);
        Assert.Contains("invalid repository", task.RepositoryError!);
    }

    [TestMethod]
    public void ParseTasks_AbsentRepositoryAndNameAreOptional()
    {
        using var json = JsonDocument.Parse("""
            {"tasks":[{"id":"x","state":"queued","html_url":"https://github.com/copilot/tasks/x","created_at":"2026-10-01T00:00:00Z"}]}
            """);

        var task = AgentsClient.ParseTasks(json.RootElement, Account.Host).Single();

        Assert.AreEqual("Agent task", task.Title);
        Assert.IsNull(task.RepositoryId);
    }

    private static RecordingHandler Handler(
        string? tasks = null,
        Uri? next = null,
        HttpStatusCode listStatus = HttpStatusCode.OK,
        HttpStatusCode detailsStatus = HttpStatusCode.OK,
        HttpStatusCode repositoryStatus = HttpStatusCode.OK) => new(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path.StartsWith("/repositories/", StringComparison.Ordinal)
                ? Response(repositoryStatus, """{"full_name":"microsoft/PowerToys"}""")
                : path.StartsWith("/agents/tasks/", StringComparison.Ordinal)
                    ? Response(detailsStatus, DetailsJson)
                    : Response(listStatus, tasks ?? $"{{\"tasks\":[{TaskJson}]}}");
            if (next is not null && path == "/agents/tasks")
            {
                response.Headers.Add("Link", $"<{next.AbsoluteUri}>; rel=\"next\"");
            }

            return response;
        });

    private static HttpResponseMessage Response(HttpStatusCode status, string payload) =>
        new(status) { Content = new StringContent(payload) };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Paths { get; } = new();

        public ConcurrentQueue<(string Path, string? Token, string Accept, string Version, bool HasUserAgent)> Headers { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Enqueue(request.RequestUri!.PathAndQuery);
            Headers.Enqueue((request.RequestUri.AbsolutePath, request.Headers.Authorization?.Parameter,
                string.Join(",", request.Headers.Accept), request.Headers.GetValues("X-GitHub-Api-Version").Single(),
                request.Headers.UserAgent.Count > 0));
            return System.Threading.Tasks.Task.FromResult(respond(request));
        }

    }

    private sealed class RequestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await respond(request).ConfigureAwait(false);
        }
    }

    private sealed class ThrottledHandler : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private int _active;

        public TaskCompletionSource SixStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started { get; private set; }

        public int MaximumActive { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/agents/tasks")
            {
                var tasks = Enumerable.Range(1, 12).Select(i => $$"""
                    {"id":"task-{{i}}","state":"queued","html_url":"https://github.com/copilot/tasks/task-{{i}}","created_at":"2026-10-01T00:00:00Z"}
                    """);
                return Response(HttpStatusCode.OK, $"{{\"tasks\":[{string.Join(',', tasks)}]}}");
            }

            lock (_lock)
            {
                Started++;
                _active++;
                MaximumActive = Math.Max(MaximumActive, _active);
                if (Started == 6)
                {
                    SixStarted.SetResult();
                }
            }

            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return Response(HttpStatusCode.OK, DetailsJson);
            }
            finally
            {
                lock (_lock)
                {
                    _active--;
                }
            }
        }
    }
}
