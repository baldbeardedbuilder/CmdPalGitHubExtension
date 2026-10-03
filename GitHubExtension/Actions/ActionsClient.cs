// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal interface IActionsClient
{
    Task<WorkflowRunsPageResult> GetRunsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken);
    Task<GitHubWorkflowRun> GetRunAsync(GitHubAccount account, string repository, long runId, CancellationToken cancellationToken);
    Task CancelRunAsync(GitHubAccount account, string repository, long runId, bool force, CancellationToken cancellationToken);
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
        var uri = RunUri(account, repository, runId);
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseRun(json.RootElement);
    }

    public async Task CancelRunAsync(GitHubAccount account, string repository, long runId, bool force, CancellationToken cancellationToken)
    {
        var endpoint = force ? "force-cancel" : "cancel";
        using var response = await SendAsync(
            httpClient, account, HttpMethod.Post, RunUri(account, repository, runId, endpoint), cancellationToken).ConfigureAwait(false);
    }

    internal static List<GitHubWorkflowRun> ParseRuns(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("workflow_runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a workflow run list we couldn't read.");
        }

        var result = new List<GitHubWorkflowRun>();
        foreach (var run in runs.EnumerateArray())
        {
            result.Add(ParseRun(run));
        }

        return result;
    }

    internal static GitHubWorkflowRun ParseRun(JsonElement run)
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
            GetString(run, "head_sha"), GetOptionalInt(run, "run_number"), GetOptionalInt(run, "run_attempt"),
            GetDate(run, "updated_at"));
    }

    private static Uri RunUri(GitHubAccount account, string repository, long runId, string? endpoint = null)
    {
        var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        return new Uri(account.Host.ApiUrl, $"repos/{path}/actions/runs/{runId}{(endpoint is null ? string.Empty : $"/{endpoint}")}");
    }

    private static int? GetOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
