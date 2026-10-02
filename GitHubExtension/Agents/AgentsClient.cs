// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Agents;

internal interface IAgentsClient
{
    Task<AgentTasksPageResult> GetTasksAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken);
}

internal sealed class AgentsClient(HttpClient httpClient) : IAgentsClient
{
    private const string ApiVersion = "2026-03-10";
    private const int MaxConcurrentRequests = 6;

    public async Task<AgentTasksPageResult> GetTasksAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken)
    {
        var uri = page ?? new Uri(account.Host.ApiUrl, "agents/tasks?per_page=30&sort=updated_at&direction=desc&is_archived=false");
        using var response = await SendAgentsAsync(account, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var tasks = ParseTasks(json.RootElement, account.Host);
        var nextPage = NextPage(response);

        using var throttle = new SemaphoreSlim(MaxConcurrentRequests);
        var repositories = new ConcurrentDictionary<long, Lazy<Task<string>>>();
        var enriched = await Task.WhenAll(tasks.Select(EnrichAsync)).ConfigureAwait(false);
        return new AgentTasksPageResult(enriched, nextPage);

        async Task<GitHubAgentTask> EnrichAsync(GitHubAgentTask task)
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            var errors = new List<string>();
            if (task.RepositoryError is { } repositoryError)
            {
                errors.Add($"Couldn't load the repository. {repositoryError}");
            }

            try
            {
                if (task.RepositoryId is { } repositoryId)
                {
                    try
                    {
                        var name = await repositories.GetOrAdd(repositoryId, id => new Lazy<Task<string>>(
                            () => GetRepositoryNameAsync(account, id, cancellationToken))).Value.ConfigureAwait(false);
                        task = task with { RepositoryFullName = name };
                    }
                    catch (Exception ex) when (ex is GitHubApiException or IOException
                        || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        errors.Add($"Couldn't load the repository. {ex.Message}");
                    }
                }

                try
                {
                    var detailsUri = new Uri(account.Host.ApiUrl, $"agents/tasks/{Uri.EscapeDataString(task.Id)}");
                    using var detailsResponse = await SendAgentsAsync(account, detailsUri, cancellationToken).ConfigureAwait(false);
                    using var details = await ReadJsonAsync(detailsResponse, cancellationToken).ConfigureAwait(false);
                    task = task with { Model = ParseModel(details.RootElement) };
                }
                catch (Exception ex) when (ex is GitHubApiException or IOException
                    || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    errors.Add($"Couldn't load the model. {ex.Message}");
                }

                return task with { DetailsError = errors.Count == 0 ? null : string.Join(" ", errors) };
            }
            finally
            {
                throttle.Release();
            }
        }
    }

    internal static List<GitHubAgentTask> ParseTasks(JsonElement root, GitHubHost host)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tasks", out var tasks)
            || tasks.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back an agent list we couldn't read.");
        }

        var result = new List<GitHubAgentTask>();
        foreach (var element in tasks.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || GetString(element, "id") is not { Length: > 0 } id
                || GetString(element, "state") is not { Length: > 0 } state)
            {
                throw new GitHubApiException("GitHub sent back an agent task we couldn't read.");
            }

            var webUrl = element.TryGetProperty("html_url", out var url) && url.ValueKind != JsonValueKind.Null
                ? GetUri(element, "html_url")
                : new Uri(host.WebUrl, $"copilot/tasks/{Uri.EscapeDataString(id)}");
            if (webUrl is null
                || webUrl.Scheme != Uri.UriSchemeHttps
                || !string.Equals(webUrl.Authority, host.WebUrl.Authority, StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(webUrl.UserInfo))
            {
                throw new GitHubApiException("GitHub sent back an agent task we couldn't read.");
            }

            var updatedAt = GetDate(element, "updated_at");
            if (updatedAt == DateTimeOffset.MinValue)
            {
                updatedAt = GetDate(element, "created_at");
            }

            if (updatedAt == DateTimeOffset.MinValue)
            {
                throw new GitHubApiException("GitHub sent back an agent task without a valid timestamp.");
            }

            long? repositoryId = null;
            string? repositoryError = null;
            if (element.TryGetProperty("repository", out var repository) && repository.ValueKind != JsonValueKind.Null)
            {
                if (repository.ValueKind != JsonValueKind.Object
                    || !repository.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number
                    || !value.TryGetInt64(out var number) || number <= 0)
                {
                    repositoryError = "GitHub sent back an agent task with an invalid repository.";
                }
                else
                {
                    repositoryId = number;
                }
            }

            result.Add(new GitHubAgentTask(id, GetString(element, "name") is { Length: > 0 } name ? name : "Agent task",
                webUrl, state, updatedAt, repositoryId, RepositoryError: repositoryError));
        }

        return result;
    }

    internal static string? ParseModel(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("sessions", out var sessions)
            || sessions.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back agent details we couldn't read.");
        }

        if (sessions.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.Object))
        {
            throw new GitHubApiException("GitHub sent back an agent session we couldn't read.");
        }

        var latest = sessions.EnumerateArray()
            .OrderByDescending(s => GetDate(s, "created_at"))
            .FirstOrDefault();
        return latest.ValueKind == JsonValueKind.Object ? GetString(latest, "model") : null;
    }

    private async Task<string> GetRepositoryNameAsync(GitHubAccount account, long id, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Host.ApiUrl, $"repositories/{id.ToString(CultureInfo.InvariantCulture)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return json.RootElement.ValueKind == JsonValueKind.Object && GetString(json.RootElement, "full_name") is { Length: > 0 } name
            ? name
            : throw new GitHubApiException("GitHub sent back a repository we couldn't read.");
    }

    private async Task<HttpResponseMessage> SendAgentsAsync(GitHubAccount account, Uri uri, CancellationToken cancellationToken)
    {
        var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken, throwOnError: false, apiVersion: ApiVersion).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new GitHubApiException("GitHub didn't accept your token. Sign out and back in to fix it."),
                HttpStatusCode.Forbidden => new GitHubApiException("GitHub denied access to agents. Check your Copilot access, token permissions (Agent tasks: read for fine-grained tokens), and API rate limit."),
                HttpStatusCode.NotFound => new GitHubApiException("The Agent Tasks API isn't available for this account or GitHub host."),
                HttpStatusCode.TooManyRequests => new GitHubApiException("GitHub's rate limit was reached. Try refreshing agents later."),
                _ => new GitHubApiException($"{account.Host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}."),
            };
        }
    }
}
