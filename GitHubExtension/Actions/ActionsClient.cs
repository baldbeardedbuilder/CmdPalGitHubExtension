// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal interface IActionsClient
{
    Task<WorkflowRunsPageResult> GetRunsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken);
    Task<GitHubWorkflowRun> GetRunAsync(GitHubAccount account, string repository, long runId, CancellationToken cancellationToken);
    Task RerunAsync(GitHubAccount account, string repository, long runId, bool failedOnly, bool debugLogging, CancellationToken cancellationToken);
}

internal sealed class ActionsClient(HttpClient httpClient) : IActionsClient
{
    private const string TimeoutMessage = "GitHub took too long to return workflow runs. Try refreshing.";

    public async Task<WorkflowRunsPageResult> GetRunsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken)
    {
        var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        var uri = page ?? new Uri(account.Host.ApiUrl, $"repos/{path}/actions/runs?per_page=50");
        try
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken, timeoutMessage: TimeoutMessage).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new WorkflowRunsPageResult(ParseRuns(json.RootElement), NextPage(response));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(TimeoutMessage, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException("The connection closed while loading workflow runs. Try refreshing.", ex);
        }
    }

    public async Task<GitHubWorkflowRun> GetRunAsync(GitHubAccount account, string repository, long runId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, RunUri(account, repository, runId), cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var run = ParseRun(json.RootElement);
        if (run.Id != runId)
        {
            throw new GitHubApiException("GitHub returned a different workflow run. Refresh and try again.");
        }

        return run;
    }

    public async Task RerunAsync(GitHubAccount account, string repository, long runId, bool failedOnly, bool debugLogging, CancellationToken cancellationToken)
    {
        var uri = new Uri(RunUri(account, repository, runId).AbsoluteUri + (failedOnly ? "/rerun-failed-jobs" : "/rerun"));
        using var content = new StringContent(
            debugLogging ? """{"enable_debug_logging":true}""" : """{"enable_debug_logging":false}""",
            Encoding.UTF8, "application/json");
        using var response = await SendAsync(httpClient, account, HttpMethod.Post, uri, cancellationToken, content: content,
            timeoutMessage: "The rerun request timed out. It may have been accepted. Refresh the run before trying again.").ConfigureAwait(false);
    }

    private static Uri RunUri(GitHubAccount account, string repository, long runId)
    {
        var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        return new Uri(account.Host.ApiUrl, $"repos/{path}/actions/runs/{runId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    internal static List<GitHubWorkflowRun> ParseRuns(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("workflow_runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a workflow run list we couldn't read.");
        }

        return runs.EnumerateArray().Select(ParseRun).ToList();
    }

    private static GitHubWorkflowRun ParseRun(JsonElement run)
    {
        if (run.ValueKind != JsonValueKind.Object
            || !run.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var number)
            || GetUri(run, "html_url") is not { } url)
        {
            throw new GitHubApiException("GitHub sent back a workflow run we couldn't read.");
        }

        var actor = run.TryGetProperty("actor", out var user) && user.ValueKind == JsonValueKind.Object
            ? GetString(user, "login") ?? string.Empty
            : string.Empty;
        return new GitHubWorkflowRun(
            number, GetString(run, "name") ?? "Workflow",
            GetString(run, "display_title") ?? GetString(run, "name") ?? "Workflow run",
            actor, GetString(run, "status") ?? "unknown", GetString(run, "conclusion"),
            GetDate(run, "created_at"), url, GetString(run, "event"), GetString(run, "head_branch"),
            GetString(run, "head_sha"), GetOptionalInt(run, "run_number"), GetOptionalInt(run, "run_attempt"), GetDate(run, "updated_at"));
    }

    private static int? GetOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
