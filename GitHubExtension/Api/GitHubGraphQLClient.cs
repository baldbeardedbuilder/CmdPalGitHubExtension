// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Api;

internal interface IGitHubGraphQLClient
{
    Task<GraphQLResult> ExecuteAsync(
        GitHubAccount account,
        string query,
        JsonObject? variables,
        CancellationToken cancellationToken,
        string? operationName = null);

    Task<GraphQLNodeResult> GetNodeIdAsync(
        GitHubAccount account,
        string repository,
        int number,
        GitHubNodeKind kind,
        CancellationToken cancellationToken);
}

internal sealed class GitHubGraphQLClient(HttpClient httpClient) : IGitHubGraphQLClient
{
    public async Task<GraphQLResult> ExecuteAsync(
        GitHubAccount account,
        string query,
        JsonObject? variables,
        CancellationToken cancellationToken,
        string? operationName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        using var content = JsonContent.Create(
            new GraphQLRequest(query, variables, operationName), GitHubJsonContext.Default.GraphQLRequest);
        using var response = await GitHubRest.SendAsync(
            httpClient, account, HttpMethod.Post, account.Host.GraphQLUrl, cancellationToken,
            content: content).ConfigureAwait(false);
        using var json = await GitHubRest.ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return GraphQLResult.Parse(json.RootElement);
    }

    public async Task<GraphQLNodeResult> GetNodeIdAsync(
        GitHubAccount account,
        string repository,
        int number,
        GitHubNodeKind kind,
        CancellationToken cancellationToken)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new GitHubApiException("The repository name must be in owner/name format.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        var field = kind switch
        {
            GitHubNodeKind.PullRequest => "pullRequest",
            GitHubNodeKind.Discussion => "discussion",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var query = $$"""
            query NodeId($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                {{field}}(number: $number) { id }
              }
            }
            """;
        var result = await ExecuteAsync(
            account, query, new JsonObject { ["owner"] = parts[0], ["name"] = parts[1], ["number"] = number },
            cancellationToken, "NodeId").ConfigureAwait(false);

        var nodeId = result.Data is { } data
            && data.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.Object
            && repo.TryGetProperty(field, out var node) && node.ValueKind == JsonValueKind.Object
                ? GitHubRest.GetString(node, "id")
                : null;
        return new GraphQLNodeResult(nodeId, result);
    }
}

internal enum GitHubNodeKind
{
    PullRequest,
    Discussion,
}

internal sealed record GraphQLNodeResult(string? NodeId, GraphQLResult Response);
