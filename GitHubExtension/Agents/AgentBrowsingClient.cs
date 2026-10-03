using System.Text.Json;
using System.Text;
using System.Text.Json.Serialization;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Agents;

internal sealed record AgentArtifactGraphQuery([property: JsonPropertyName("query")] string Query);
[JsonSerializable(typeof(AgentArtifactGraphQuery))]
internal sealed partial class AgentBrowsingJsonContext : JsonSerializerContext;

internal sealed partial class AgentsClient
{
    public Task<GitHubAgentTask> GetTaskAsync(GitHubAccount account, GitHubAgentTask task, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Agents, async () =>
        {
            using var response = await SendAgentsAsync(account,
                new Uri(account.Host.ApiUrl, $"agents/tasks/{Uri.EscapeDataString(task.Id)}"), token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var details = WithDetails(task, json.RootElement);
            var artifacts = new List<AgentArtifact>();
            foreach (var artifact in details.Artifacts ?? [])
            {
                if (artifact.Provider == "github" && artifact.Type == "pull" && artifact.GlobalId is { Length: > 0 } id)
                {
                    try
                    {
                        var query = $"query {{ node(id: {GitHubJson.String(id)}) {{ ... on PullRequest {{ url }} }} }}";
                        using var content = new StringContent(JsonSerializer.Serialize(new AgentArtifactGraphQuery(query),
                            AgentBrowsingJsonContext.Default.AgentArtifactGraphQuery), Encoding.UTF8, "application/json");
                        using var graphResponse = await SendAsync(httpClient, account, HttpMethod.Post, account.Host.GraphQLUrl, token, content: content, isMutation: false).ConfigureAwait(false);
                        using var graph = await ReadJsonAsync(graphResponse, token).ConfigureAwait(false);
                        if (graph.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                            && data.TryGetProperty("node", out var node) && node.ValueKind == JsonValueKind.Object
                            && GetUri(node, "url") is { } url && url.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(url.UserInfo)
                            && url.Authority.Equals(account.Host.WebUrl.Authority, StringComparison.OrdinalIgnoreCase))
                        {
                            artifacts.Add(artifact with { WebUrl = url });
                            continue;
                        }
                    }
                    catch (Exception ex) when (ex is GitHubApiException or IOException)
                    {
                    }
                }

                artifacts.Add(artifact);
            }

            return details with { Artifacts = artifacts };
        }, cancellationToken: token);

    internal static Uri QueryUri(GitHubAccount account, AgentQuery query)
    {
        if (query.State is { Length: > 0 } state && state.Split(',').Any(s => !AgentQuery.States.Contains(s)))
        {
            throw new GitHubApiException("Choose a supported agent task state.");
        }

        var path = "agents/tasks";
        if (!string.IsNullOrWhiteSpace(query.Repository))
        {
            _ = RepositoriesClient.WatchingUri(account, query.Repository);
            path = $"agents/repos/{string.Join('/', query.Repository.Split('/').Select(Uri.EscapeDataString))}/tasks";
        }

        return new(account.Host.ApiUrl, $"{path}?per_page=30&sort=updated_at&direction=desc&is_archived={query.Archived.ToString().ToLowerInvariant()}"
            + (string.IsNullOrEmpty(query.State) ? string.Empty : $"&state={Uri.EscapeDataString(query.State)}"));
    }

    internal static void ValidatePage(Uri page, Uri first)
    {
        var actual = System.Web.HttpUtility.ParseQueryString(page.Query);
        var expected = System.Web.HttpUtility.ParseQueryString(first.Query);
        if (page.GetLeftPart(UriPartial.Path) != first.GetLeftPart(UriPartial.Path)
            || !string.IsNullOrEmpty(page.UserInfo) || !string.IsNullOrEmpty(page.Fragment)
            || expected.AllKeys.Any(key => actual[key] != expected[key])
            || actual.AllKeys.Any(key => key != "page" && !expected.AllKeys.Contains(key))
            || actual["page"] is { } number && (!int.TryParse(number, out var n) || n < 1))
        {
            throw new GitHubApiException("GitHub sent back an agent page outside the selected scope.");
        }
    }

    internal static GitHubAgentTask WithDetails(GitHubAgentTask task, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) { throw new GitHubApiException("GitHub sent back task details we couldn't read."); }
        if (GetString(root, "id") is { } id && id != task.Id) { throw new GitHubApiException("GitHub returned details for a different task."); }
        task = task with
        {
            Title = GetString(root, "name") ?? task.Title,
            State = GetString(root, "state") ?? task.State,
            UpdatedAt = GetDate(root, "updated_at") is var updated && updated != DateTimeOffset.MinValue ? updated : task.UpdatedAt,
            ArchivedAt = root.TryGetProperty("archived_at", out _) && GetDate(root, "archived_at") is var archived
                ? archived == DateTimeOffset.MinValue ? null : archived : task.ArchivedAt,
        };
        if (!root.TryGetProperty("sessions", out var returned) || returned.ValueKind == JsonValueKind.Null)
        {
            return task with { Sessions = [], Artifacts = root.TryGetProperty("artifacts", out _) ? ParseArtifacts(root) : task.Artifacts, DetailsError = null };
        }

        var model = ParseModel(root);
        var sessions = returned.EnumerateArray().Select(s =>
        {
            string? usageType = null;
            double? amount = null;
            if (s.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                usageType = GetString(usage, "type");
                if (usage.TryGetProperty("amount", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0)
                {
                    amount = number;
                }
            }

            var error = s.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object ? GetString(e, "message") : null;
            return new AgentSession(GetString(s, "id") ?? string.Empty, GetString(s, "name"), GetString(s, "state") ?? "unknown",
                GetString(s, "prompt"), GetString(s, "model"), GetDate(s, "created_at"), GetDate(s, "updated_at"),
                GetString(s, "head_ref"), GetString(s, "base_ref"), error, usageType, amount);
        }).ToArray();
        return task with
        {
            Model = model,
            Sessions = sessions,
            Artifacts = root.TryGetProperty("artifacts", out _) ? ParseArtifacts(root) : task.Artifacts,
            DetailsError = null,
        };
    }

    internal static IReadOnlyList<AgentArtifact> ParseArtifacts(JsonElement root)
    {
        if (!root.TryGetProperty("artifacts", out var artifacts) || artifacts.ValueKind == JsonValueKind.Null) { return []; }
        if (artifacts.ValueKind != JsonValueKind.Array) { throw new GitHubApiException("GitHub sent back artifacts we couldn't read."); }
        return artifacts.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object).Select(a =>
        {
            var data = a.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            return new AgentArtifact(GetString(a, "provider") ?? "unknown", GetString(a, "type") ?? "unknown",
                data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var n) ? n : null,
                data.ValueKind == JsonValueKind.Object ? GetString(data, "global_id") : null,
                data.ValueKind == JsonValueKind.Object ? GetString(data, "head_ref") : null,
                data.ValueKind == JsonValueKind.Object ? GetString(data, "base_ref") : null);
        }).ToArray();
    }
}
