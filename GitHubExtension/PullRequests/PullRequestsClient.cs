// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal interface IPullRequestsClient
{
    Task<PullRequestsPageResult> GetPullRequestsAsync(
        GitHubAccount account,
        string repository,
        Uri? page,
        CancellationToken cancellationToken);
}

internal sealed class PullRequestsClient(HttpClient httpClient) : IPullRequestsClient, IIssueConversationClient
{
    internal const int PageSize = 100;

    public Task<PullRequestsPageResult> GetPullRequestsAsync(
        GitHubAccount account,
        string repository,
        Uri? page,
        CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.PullRequests, async () =>
    {
        var uri = page ?? RepositoryPullRequestsUri(account, repository);
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new PullRequestsPageResult(ParsePullRequests(json.RootElement), NextPage(response));
    }, cancellationToken: cancellationToken);

    public Task<IssueCommentsPage> GetCommentsAsync(
        GitHubAccount account, string repository, int number, Uri? page, CancellationToken token) =>
        new IssueConversationClient(httpClient, DiagnosticArea.PullRequests).GetCommentsAsync(account, repository, number, page, token);

    public Task<IssueComment> CreateCommentAsync(
        GitHubAccount account, string repository, int number, string body, CancellationToken token) =>
        new IssueConversationClient(httpClient, DiagnosticArea.PullRequests).CreateCommentAsync(account, repository, number, body, token);

    public Task<IssueComment> EditCommentAsync(
        GitHubAccount account, string repository, int number, long commentId, string body, CancellationToken token) =>
        new IssueConversationClient(httpClient, DiagnosticArea.PullRequests).EditCommentAsync(account, repository, number, commentId, body, token);

    public Task DeleteCommentAsync(
        GitHubAccount account, string repository, int number, long commentId, CancellationToken token) =>
        new IssueConversationClient(httpClient, DiagnosticArea.PullRequests).DeleteCommentAsync(account, repository, number, commentId, token);

    internal static List<GitHubPullRequest> ParsePullRequests(JsonElement array) =>
        DomainDiagnostics.Read(DiagnosticArea.PullRequests, () =>
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a pull request list we couldn't read.");
        }

        var pullRequests = new List<GitHubPullRequest>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new GitHubApiException("GitHub sent back a pull request we couldn't read.");
            }

            pullRequests.Add(GitHubPullRequest.Parse(element, SubjectStateParser.Parse(element)));
        }

        return pullRequests;
    });

    private static Uri RepositoryPullRequestsUri(GitHubAccount account, string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new GitHubApiException("The repository name must be in owner/name format.");
        }

        return new Uri(
            account.Host.ApiUrl,
            $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/pulls?state=all&sort=created&direction=desc&per_page={PageSize}");
    }
}

internal sealed record PullRequestsPageResult(IReadOnlyList<GitHubPullRequest> PullRequests, Uri? NextPage);
