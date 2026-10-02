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
}

internal sealed class ActionsClient(HttpClient httpClient) : IActionsClient
{
    public async Task<WorkflowRunsPageResult> GetRunsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken)
    {
        var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        var uri = page ?? new Uri(account.Host.ApiUrl, $"repos/{path}/actions/runs?per_page=50");
        try
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new WorkflowRunsPageResult(ParseRuns(json.RootElement), NextPage(response));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException("GitHub took too long to return workflow runs. Try refreshing.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException("The connection closed while loading workflow runs. Try refreshing.", ex);
        }
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
            int? runNumber = GetOptionalInt(run, "run_number");
            int? runAttempt = GetOptionalInt(run, "run_attempt");
            result.Add(new GitHubWorkflowRun(
                number, GetString(run, "name") ?? "Workflow",
                GetString(run, "display_title") ?? GetString(run, "name") ?? "Workflow run",
                actor, GetString(run, "status") ?? "unknown", GetString(run, "conclusion"),
                GetDate(run, "created_at"), url, GetString(run, "event"), GetString(run, "head_branch"),
                GetString(run, "head_sha"), runNumber, runAttempt, GetDate(run, "updated_at")));
        }

        return result;
    }

    private static int? GetOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
