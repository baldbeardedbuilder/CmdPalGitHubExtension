// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public sealed class DomainDiagnosticsTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "private-user", "test-token");
    private const string Space = """
        {"name":"private-space","web_url":"https://private-space.github.dev",
         "repository":{"full_name":"private-owner/private-repo"},"state":"Available"}
        """;
    private const string Agent = """
        {"id":"private-task","state":"in_progress","repository":{"id":987654},
         "updated_at":"2026-10-02T10:00:00Z"}
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("Actions", "{}")]
    [DataRow("Actions", """{"workflow_runs":[{}]}""")]
    [DataRow("Agents", "[]")]
    [DataRow("Agents", """{"tasks":[{}]}""")]
    [DataRow("AgentModel", """{"sessions":[42]}""")]
    [DataRow("Codespaces", """{"codespaces":{}}""")]
    [DataRow("Issues", "{}")]
    [DataRow("Issues", "[{}]")]
    [DataRow("PullRequests", "{}")]
    [DataRow("PullRequests", "[{}]")]
    [DataRow("Notifications", "{}")]
    [DataRow("Notifications", "[{}]")]
    [DataRow("Repositories", "{}")]
    [DataRow("Repositories", "[{}]")]
    [DataRow("Search", """{"items":null}""")]
    [DataRow("Search", """{"items":[],"incomplete_results":"invalid"}""")]
    public void WrongShape_ReportsSchemaWithoutPayloadOrDuplicateErrors(string parser, string payload)
    {
        using var json = JsonDocument.Parse(payload);
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);

        Assert.ThrowsExactly<GitHubApiException>(() =>
        {
            switch (parser)
            {
                case "Actions": ActionsClient.ParseRuns(json.RootElement); break;
                case "Agents": AgentsClient.ParseTasks(json.RootElement, Account.Host); break;
                case "AgentModel": AgentsClient.ParseModel(json.RootElement); break;
                case "Codespaces": CodespacesClient.ParseCodespaces(json.RootElement); break;
                case "Issues": IssuesClient.ParseIssues(json.RootElement); break;
                case "PullRequests": PullRequestsClient.ParsePullRequests(json.RootElement); break;
                case "Notifications": NotificationsClient.ParseNotifications(json.RootElement); break;
                case "Repositories": RepositoriesClient.ParseRepositories(json.RootElement); break;
                case "Search": RepositoriesClient.ParseSearch(json.RootElement); break;
            }
        });

        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticEvent.SchemaRead, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Schema, failure.Failure);
        Assert.AreEqual(DiagnosticOutcome.Failed, failure.Outcome);
        Assert.IsTrue(entries.All(entry => entry.OperationId == failure.OperationId));
        Assert.IsFalse(entries.Any(entry => entry.ToString().Contains("couldn't read", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SkippedCodespace_StillReportsSchemaAndPreservesTolerantParsing()
    {
        using var json = JsonDocument.Parse("""{"codespaces":[{"name":"private-invalid"}]}""");
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);

        Assert.HasCount(0, CodespacesClient.ParseCodespaces(json.RootElement));

        Assert.AreEqual(DiagnosticFailure.Schema, entries.Single().Failure);
        Assert.IsFalse(entries.Single().ToString().Contains("private-invalid", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WrongSubjectRoot_IsClassifiedAsSchemaEvenWhenJsonAccessThrows()
    {
        using var json = JsonDocument.Parse("[]");
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);

        Assert.ThrowsExactly<InvalidOperationException>(() => NotificationsClient.ParseSubject(json.RootElement));

        var failure = entries.Single();
        Assert.AreEqual(DiagnosticArea.Notifications, failure.Area);
        Assert.AreEqual(DiagnosticEvent.SchemaRead, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Schema, failure.Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateCodespace_CorrelatesLookupPostAndSchemaAndUsesReturnedState(bool pending)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue, verboseReads: true);
        using var handler = new Handler((request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Response("""{"id":987654}""")
            : Response(pending ? Space.Replace("Available", "Queued", StringComparison.Ordinal) : Space, HttpStatusCode.Created)));
        using var http = new HttpClient(handler);

        await new CodespacesClient(http).CreateCodespaceAsync(Account, "private-owner/private-repo", "private-branch", TestContext.CancellationToken);

        Assert.HasCount(2, entries.Where(entry => entry.Event == DiagnosticEvent.RestRequest && entry.Outcome == DiagnosticOutcome.Requested));
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        Assert.IsTrue(entries.All(entry => entry.Area == DiagnosticArea.Codespaces));
        Assert.AreEqual(pending ? DiagnosticOutcome.Accepted : DiagnosticOutcome.Completed,
            entries.Last(entry => entry.Event == DiagnosticEvent.Mutation).Outcome);
        Assert.IsFalse(entries.Any(entry => entry.ToString().Contains("private-", StringComparison.Ordinal)));
        Assert.IsFalse(entries.Any(entry => entry.ToString().Contains("987654", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("start", "Starting", false)]
    [DataRow("start", "Available", true)]
    [DataRow("stop", "ShuttingDown", false)]
    [DataRow("stop", "Shutdown", true)]
    public async Task CodespaceMutation_PendingStateIsOnlyAccepted(string action, string state, bool completed)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Space.Replace("Available", state, StringComparison.Ordinal))));
        using var http = new HttpClient(handler);
        var client = new CodespacesClient(http);

        if (action == "start")
        {
            await client.StartCodespaceAsync(Account, "private-space", TestContext.CancellationToken);
        }
        else
        {
            await client.StopCodespaceAsync(Account, "private-space", TestContext.CancellationToken);
        }

        Assert.AreEqual(DiagnosticOutcome.Requested, entries.First(entry => entry.Event == DiagnosticEvent.Mutation).Outcome);
        Assert.AreEqual(completed ? DiagnosticOutcome.Completed : DiagnosticOutcome.Accepted,
            entries.Last(entry => entry.Event == DiagnosticEvent.Mutation).Outcome);
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
    }

    [TestMethod]
    [DataRow("network", "Unknown", "Transport")]
    [DataRow("timeout", "Unknown", "Timeout")]
    [DataRow("schema", "Unknown", "Schema")]
    [DataRow("http", "Failed", "Http")]
    public async Task MutationFailures_DistinguishRejectionFromUncertainResult(string failure, string expected, string category)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var handler = new Handler((_, _) => failure switch
        {
            "network" => Task.FromException<HttpResponseMessage>(new HttpRequestException("private-network-detail")),
            "timeout" => Task.FromException<HttpResponseMessage>(new TaskCanceledException("private-timeout-detail")),
            "schema" => Task.FromResult(Response("{}")),
            _ => Task.FromResult(Response("private-response-body", HttpStatusCode.Forbidden)),
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(Account, "private-space", TestContext.CancellationToken));

        var terminal = entries.Last(entry => entry.Event == DiagnosticEvent.Mutation);
        Assert.AreEqual(expected, terminal.Outcome.ToString());
        Assert.AreEqual(category, terminal.Failure.ToString());
        Assert.IsFalse(entries.Any(entry => entry.ToString().Contains("private-", StringComparison.Ordinal)));
        Assert.IsFalse(entries.Any(entry => entry.Event == DiagnosticEvent.Mutation && entry.Outcome == DiagnosticOutcome.Completed));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CreateLookupFailure_IsFailedRatherThanUnknownAndDoesNotPost(bool schemaFailure)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        var requests = 0;
        using var handler = new Handler((_, _) =>
        {
            requests++;
            return schemaFailure ? Task.FromResult(Response("""{"id":"private-invalid"}"""))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("private-detail"));
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new CodespacesClient(http).CreateCodespaceAsync(Account, "private-owner/private-repo", null, TestContext.CancellationToken));

        Assert.AreEqual(1, requests);
        Assert.AreEqual(DiagnosticOutcome.Failed, entries.Last(entry => entry.Event == DiagnosticEvent.Mutation).Outcome);
    }

    [TestMethod]
    public async Task CallerCancellation_IsCancelledWithoutErrorOrCompletion()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var cancellation = new CancellationTokenSource();
        using var handler = new Handler((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new CodespacesClient(http).StartCodespaceAsync(Account, "private-space", cancellation.Token));

        Assert.AreEqual(DiagnosticOutcome.Cancelled, entries.Last(entry => entry.Event == DiagnosticEvent.Mutation).Outcome);
        Assert.IsFalse(entries.Any(entry => entry.Severity == DiagnosticSeverity.Error));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AgentEnrichment_IsCorrelatedAndPartialFailureIsNotSuccessfulLoad(bool partial)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue, verboseReads: true);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath switch
        {
            "/agents/tasks" => $"{{\"tasks\":[{Agent}]}}",
            "/repositories/987654" => partial ? "{}" : """{"full_name":"private-owner/private-repo"}""",
            _ => """{"sessions":[{"model":"private-model"}]}""",
        })));
        using var http = new HttpClient(handler);

        var result = await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken);

        Assert.HasCount(3, entries.Where(entry => entry.Event == DiagnosticEvent.RestRequest && entry.Outcome == DiagnosticOutcome.Requested));
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        Assert.IsTrue(entries.All(entry => entry.Area == DiagnosticArea.Agents));
        Assert.AreEqual(partial, result.Tasks.Single().DetailsError is not null);
        Assert.AreEqual(partial ? DiagnosticOutcome.Partial : DiagnosticOutcome.Completed,
            entries.Last(entry => entry.Event == DiagnosticEvent.PageLoad).Outcome);
        Assert.IsFalse(entries.Any(entry => entry.ToString().Contains("private-", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(202, "Accepted")]
    [DataRow(200, "Accepted")]
    [DataRow(204, "Completed")]
    [DataRow(205, "Completed")]
    public async Task NotificationMutation_OnlySynchronousContractResponseProvesCompletion(int status, string outcome)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var handler = new Handler((_, _) => Task.FromResult(Response(string.Empty, (HttpStatusCode)status)));
        using var http = new HttpClient(handler);

        await new NotificationsClient(http).MarkAsReadAsync(Account, "private-thread", TestContext.CancellationToken);

        Assert.AreEqual(outcome, entries.Last(entry => entry.Event == DiagnosticEvent.Mutation).Outcome.ToString());
    }

    [TestMethod]
    public async Task CodespaceFailedState_ReportsFailureWithoutTreatingValidResourceAsSchemaFailure()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var handler = new Handler((_, _) => Task.FromResult(Response(Space.Replace("Available", "Failed", StringComparison.Ordinal))));
        using var http = new HttpClient(handler);

        var space = await new CodespacesClient(http).StartCodespaceAsync(Account, "private-space", TestContext.CancellationToken);

        Assert.AreEqual("Failed", space.State);
        var terminal = entries.Last(entry => entry.Event == DiagnosticEvent.Mutation);
        Assert.AreEqual(DiagnosticOutcome.Failed, terminal.Outcome);
        Assert.AreEqual(DiagnosticSeverity.Error, terminal.Severity);
        Assert.AreEqual(DiagnosticFailure.None, terminal.Failure);
    }

    [TestMethod]
    public async Task PartialAgentEnrichment_IsVisibleWithoutVerboseReads()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue, verboseReads: false);
        using var handler = new Handler((request, _) => Task.FromResult(Response(request.RequestUri!.AbsolutePath switch
        {
            "/agents/tasks" => $"{{\"tasks\":[{Agent}]}}",
            "/repositories/987654" => """{"full_name":"private-owner/private-repo"}""",
            _ => """{"sessions":{}}""",
        })));
        using var http = new HttpClient(handler);

        var result = await new AgentsClient(http).GetTasksAsync(Account, null, TestContext.CancellationToken);

        Assert.IsNotNull(result.Tasks.Single().DetailsError);
        var terminal = entries.Last(entry => entry.Event == DiagnosticEvent.PageLoad);
        Assert.AreEqual(DiagnosticOutcome.Partial, terminal.Outcome);
        Assert.AreEqual(DiagnosticSeverity.Warning, terminal.Severity);
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.SchemaRead && entry.Failure == DiagnosticFailure.Schema));
    }

    [TestMethod]
    public async Task ValidJsonWrongModel_ReportsSchemaAtDomainLoadAndPreservesMessage()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: false);
        using var handler = new Handler((_, _) => Task.FromResult(Response("""{"workflow_runs":[{"id":"private-invalid"}]}""")));
        using var http = new HttpClient(handler);

        var exception = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new ActionsClient(http).GetRunsAsync(Account, "private-owner/private-repo", null, TestContext.CancellationToken));

        Assert.AreEqual("GitHub sent back a workflow run we couldn't read.", exception.Message);
        var schema = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticEvent.SchemaRead, schema.Event);
        var terminal = entries.Last(entry => entry.Event == DiagnosticEvent.PageLoad);
        Assert.AreEqual(DiagnosticFailure.Schema, terminal.Failure);
        Assert.AreEqual(DiagnosticOutcome.Failed, terminal.Outcome);
        Assert.HasCount(1, entries.Select(entry => entry.OperationId).Distinct());
        Assert.IsFalse(entries.Any(entry => entry.ToString().Contains("private-", StringComparison.Ordinal)));
    }

    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
