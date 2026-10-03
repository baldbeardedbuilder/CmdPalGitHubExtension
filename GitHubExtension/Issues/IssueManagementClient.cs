// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal sealed record IssueMilestone(int Number, string Title, string State, int OpenIssues);

internal sealed record IssueMilestonesPage(IReadOnlyList<IssueMilestone> Milestones, Uri? NextPage);

internal interface IIssueManagementClient
{
    Task<IssueMilestonesPage> GetMilestonesAsync(GitHubAccount account, string repository, Uri? page, CancellationToken token);
    Task<GitHubIssue> CreateIssueAsync(GitHubAccount account, string repository, string title, string? body, int? milestone, CancellationToken token);
    Task<GitHubIssue> UpdateIssueAsync(GitHubAccount account, string repository, GitHubIssue expected, string title,
        string? body, int? milestone, CancellationToken token);
}

internal sealed class IssueManagementClient(HttpClient httpClient) : IIssueManagementClient
{
    private const int PageSize = 100;

    public Task<IssueMilestonesPage> GetMilestonesAsync(
        GitHubAccount account, string repository, Uri? page, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
        {
            var endpoint = new Uri(RepositoryUri(account, repository).AbsoluteUri + "/milestones");
            var uri = page ?? new Uri(endpoint.AbsoluteUri + $"?state=all&per_page={PageSize}");
            RequirePage(endpoint, uri);
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var milestones = ParseMilestones(json.RootElement);
            var next = NextPage(response);
            if (next is not null) RequirePage(endpoint, next);
            return new IssueMilestonesPage(milestones, next);
        }, cancellationToken: token);

    public Task<GitHubIssue> CreateIssueAsync(
        GitHubAccount account, string repository, string title, string? body, int? milestone, CancellationToken token) =>
        WriteIssueAsync(account, repository, null, title, body, milestone, token);

    public Task<GitHubIssue> UpdateIssueAsync(
        GitHubAccount account, string repository, GitHubIssue expected, string title, string? body, int? milestone, CancellationToken token) =>
        WriteIssueAsync(account, repository, expected, title, body, milestone, token);

    private Task<GitHubIssue> WriteIssueAsync(
        GitHubAccount account, string repository, GitHubIssue? expected, string title, string? body, int? milestone, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
        {
            if (string.IsNullOrWhiteSpace(title)) throw new GitHubApiException("Enter an issue title.");
            if (expected is { Number: <= 0 } || milestone is <= 0) throw new GitHubApiException("Choose a valid issue or milestone.");
            int? number = expected?.Number;
            if (milestone is not null)
            {
                var valid = await GetMilestoneAsync(account, repository, milestone.Value, token).ConfigureAwait(false);
                if (valid.State != "open" && milestone != expected?.MilestoneNumber)
                    throw new GitHubApiException("Choose an open milestone or remove the milestone.");
            }

            if (expected is not null)
            {
                using var currentResponse = await SendAsync(
                    httpClient, account, HttpMethod.Get, IssueUri(account, repository, expected.Number), token).ConfigureAwait(false);
                using var currentJson = await ReadJsonAsync(currentResponse, token).ConfigureAwait(false);
                var current = IssuesClient.ParseIssue(currentJson.RootElement);
                if (current.Number != expected.Number || current.WebUrl != expected.WebUrl || current.Title != expected.Title
                    || current.Body != expected.Body || current.MilestoneNumber != expected.MilestoneNumber)
                    throw new GitHubApiException("The issue changed since you opened the editor. Refresh and review the latest text before saving.");
            }

            var payload = new IssueWriteRequest(title.Trim(), body, milestone);
            using var content = JsonContent(payload);
            var uri = number is null ? new Uri(RepositoryUri(account, repository).AbsoluteUri + "/issues")
                : IssueUri(account, repository, number.Value);
            using var response = await SendMutationAsync(httpClient, account, number is null ? HttpMethod.Post : HttpMethod.Patch,
                uri, token, content: content).ConfigureAwait(false);
            if (response.IsAccepted || response.Json is null)
                throw new GitHubApiException("GitHub hasn't confirmed the issue. Refresh before trying again.", outcomeUnknown: true);
            GitHubIssue issue;
            try
            {
                issue = IssuesClient.ParseIssue(response.Json.RootElement);
            }
            catch (GitHubApiException ex)
            {
                throw new GitHubApiException("GitHub may have saved the issue, but its response couldn't be verified.",
                    ex, outcomeUnknown: true);
            }
            if (issue.Number <= 0 || number is not null && issue.Number != number.Value)
                throw new GitHubApiException("GitHub returned a different issue than the one requested.", outcomeUnknown: true);
            if (!string.Equals(issue.Title, title.Trim(), StringComparison.Ordinal)
                || !string.Equals(issue.Body, body, StringComparison.Ordinal)
                || issue.MilestoneNumber != milestone)
                throw new GitHubApiException("GitHub didn't save every issue field. Refresh the issue before trying again.",
                    outcomeUnknown: true);
            return issue;
        }, cancellationToken: token);

    private async Task<IssueMilestone> GetMilestoneAsync(
        GitHubAccount account, string repository, int number, CancellationToken token)
    {
        var uri = new Uri(RepositoryUri(account, repository).AbsoluteUri
            + $"/milestones/{number.ToString(CultureInfo.InvariantCulture)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        return ParseMilestone(json.RootElement);
    }

    private static List<IssueMilestone> ParseMilestones(JsonElement value) =>
        DomainDiagnostics.Read(DiagnosticArea.Issues, () =>
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw new GitHubApiException("GitHub sent back a milestone list we couldn't read.");
            return value.EnumerateArray().Select(ParseMilestone).ToList();
        });

    private static IssueMilestone ParseMilestone(JsonElement value) =>
        DomainDiagnostics.Read(DiagnosticArea.Issues, () =>
        {
            var state = GetString(value, "state");
            if (value.ValueKind != JsonValueKind.Object || GetInt(value, "number") <= 0
                || GetString(value, "title") is not { Length: > 0 } title || state is not ("open" or "closed"))
                throw new GitHubApiException("GitHub sent back a milestone we couldn't read.");
            return new IssueMilestone(GetInt(value, "number"), title, state, GetInt(value, "open_issues"));
        });

    private static void RequirePage(Uri endpoint, Uri page)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps || page.Authority != endpoint.Authority
            || page.AbsolutePath != endpoint.AbsolutePath || page.UserInfo.Length != 0 || page.Fragment.Length != 0)
            throw new GitHubApiException("GitHub sent back an unexpected milestone page.");
    }

    private static Uri IssueUri(GitHubAccount account, string repository, int number) =>
        new(RepositoryUri(account, repository).AbsoluteUri + $"/issues/{number.ToString(CultureInfo.InvariantCulture)}");

    private static Uri RepositoryUri(GitHubAccount account, string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.Any(char.IsWhiteSpace) || part.Contains('\\')))
            throw new GitHubApiException("The repository name must be in owner/name format.");
        return new Uri(account.Host.ApiUrl,
            $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}");
    }

    private static StringContent JsonContent(IssueWriteRequest request) =>
        new(JsonSerializer.Serialize(request, IssueManagementJsonContext.Default.IssueWriteRequest),
            Encoding.UTF8, "application/json");
}
