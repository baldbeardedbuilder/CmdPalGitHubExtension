// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal interface IIssuesClient
{
    Task<IssuesPageResult> GetIssuesAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken);

    Task<GitHubIssue> GetIssueAsync(GitHubAccount account, Uri issueApiUrl, CancellationToken cancellationToken);
}

internal sealed partial class IssuesClient(HttpClient httpClient) :
    IIssuesClient, IIssueMutationsClient, IIssueManagementClient, IIssueConversationClient
{
    internal const int PageSize = 100;

    public Task<IssuesPageResult> GetIssuesAsync(
        GitHubAccount account,
        string repository,
        Uri? page,
        CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
    {
        var uri = page ?? RepositoryIssuesUri(account, repository);
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new IssuesPageResult(ParseIssues(json.RootElement), NextPage(response));
    }, cancellationToken: cancellationToken);

    public Task<GitHubIssue> GetIssueAsync(GitHubAccount account, Uri issueApiUrl, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
    {
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, issueApiUrl, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseIssue(json.RootElement);
    }, cancellationToken: cancellationToken);

    public Task<IssueMilestonesPage> GetMilestonesAsync(GitHubAccount account, string repository, Uri? page, CancellationToken token) =>
        new IssueManagementClient(httpClient).GetMilestonesAsync(account, repository, page, token);

    public Task<GitHubIssue> CreateIssueAsync(
        GitHubAccount account, string repository, string title, string? body, int? milestone, CancellationToken token) =>
        new IssueManagementClient(httpClient).CreateIssueAsync(account, repository, title, body, milestone, token);

    public Task<GitHubIssue> UpdateIssueAsync(
        GitHubAccount account, string repository, GitHubIssue expected, string title, string? body, int? milestone, CancellationToken token) =>
        new IssueManagementClient(httpClient).UpdateIssueAsync(account, repository, expected, title, body, milestone, token);

    public Task<IssueCommentsPage> GetCommentsAsync(
        GitHubAccount account, string repository, int number, Uri? page, CancellationToken token) =>
        new IssueConversationClient(httpClient).GetCommentsAsync(account, repository, number, page, token);

    public Task<IssueComment> CreateCommentAsync(
        GitHubAccount account, string repository, int number, string body, CancellationToken token) =>
        new IssueConversationClient(httpClient).CreateCommentAsync(account, repository, number, body, token);

    public Task<IssueComment> EditCommentAsync(
        GitHubAccount account, string repository, int number, long commentId, string body, CancellationToken token) =>
        new IssueConversationClient(httpClient).EditCommentAsync(account, repository, number, commentId, body, token);

    public Task DeleteCommentAsync(
        GitHubAccount account, string repository, int number, long commentId, CancellationToken token) =>
        new IssueConversationClient(httpClient).DeleteCommentAsync(account, repository, number, commentId, token);

    internal static List<GitHubIssue> ParseIssues(JsonElement array) =>
        DomainDiagnostics.Read(DiagnosticArea.Issues, () =>
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back an issue list we couldn't read.");
        }

        var issues = new List<GitHubIssue>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new GitHubApiException("GitHub sent back an issue we couldn't read.");
            }

            if (!element.TryGetProperty("pull_request", out _))
            {
                issues.Add(ParseIssue(element));
            }
        }

        return issues;
    });

    internal static GitHubIssue ParseIssue(JsonElement element) =>
        DomainDiagnostics.Read(DiagnosticArea.Issues, () =>
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
            GetInt(element, "comments"),
            element.TryGetProperty("milestone", out var milestone) && milestone.ValueKind == JsonValueKind.Object
                && GetInt(milestone, "number") is > 0 ? GetInt(milestone, "number") : null);
    });

    private static Uri RepositoryIssuesUri(GitHubAccount account, string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new GitHubApiException("The repository name must be in owner/name format.");
        }

        return new Uri(
            account.Host.ApiUrl,
            $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/issues?state=all&sort=created&direction=desc&per_page={PageSize}");
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

internal sealed record IssuesPageResult(IReadOnlyList<GitHubIssue> Issues, Uri? NextPage);
