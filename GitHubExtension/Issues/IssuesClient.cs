// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal interface IIssuesClient
{
    Task<GitHubIssue> GetIssueAsync(GitHubAccount account, Uri issueApiUrl, CancellationToken cancellationToken);
}

internal sealed class IssuesClient(HttpClient httpClient) : IIssuesClient
{
    public async Task<GitHubIssue> GetIssueAsync(GitHubAccount account, Uri issueApiUrl, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, issueApiUrl, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseIssue(json.RootElement);
    }

    internal static GitHubIssue ParseIssue(JsonElement element)
    {
        var state = GetString(element, "state");
        var subjectState = state switch
        {
            "open" => SubjectState.Open,
            "closed" when GetString(element, "state_reason") == "not_planned" => SubjectState.NotPlanned,
            "closed" => SubjectState.Closed,
            _ => SubjectState.Unknown,
        };

        return new GitHubIssue(
            GetInt(element, "number"),
            GetString(element, "title") ?? string.Empty,
            GetString(element, "body"),
            subjectState,
            GetUri(element, "html_url") ?? throw new GitHubApiException("GitHub didn't include a link for this issue."),
            GetDate(element, "created_at"),
            element.TryGetProperty("user", out var user) ? GetString(user, "login") : null,
            GetNames(element, "assignees", "login"),
            GetNames(element, "labels", "name"),
            GetInt(element, "comments"));
    }

    private static string[] GetNames(JsonElement element, string propertyName, string valueName)
    {
        if (!element.TryGetProperty(propertyName, out var values) || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value => GetString(value, valueName))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }
}
