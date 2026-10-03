// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal interface INotificationsClient
{
    /// <summary>
    /// Gets a page of notifications. Pass null for the first page, or the NextPage from a previous result.
    /// </summary>
    Task<NotificationsPageResult> GetNotificationsAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken);

    Task<SubjectDetails?> GetSubjectAsync(GitHubAccount account, Uri subjectApiUrl, CancellationToken cancellationToken);

    Task MarkAsReadAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken);

    Task MarkAsDoneAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken);
}

internal sealed class NotificationsClient(HttpClient httpClient) : INotificationsClient
{
    internal const int PageSize = 50;

    public async Task<NotificationsPageResult> GetNotificationsAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken)
    {
        var uri = page ?? new Uri(account.Host.ApiUrl, $"notifications?all=true&per_page={PageSize}");

        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        var notifications = new List<GitHubNotification>();
        if (json.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in json.RootElement.EnumerateArray())
            {
                if (ParseNotification(element) is { } notification)
                {
                    notifications.Add(notification);
                }
            }
        }

        return new NotificationsPageResult(notifications, NextPage(response));
    }

    public async Task<SubjectDetails?> GetSubjectAsync(GitHubAccount account, Uri subjectApiUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, subjectApiUrl, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseSubject(json.RootElement);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException($"The request to {subjectApiUrl.Host} timed out. Try again.", ex);
        }
    }

    public async Task MarkAsReadAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Patch, uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkAsDoneAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Delete, uri, cancellationToken).ConfigureAwait(false);
    }

    internal static GitHubNotification? ParseNotification(JsonElement element)
    {
        if (GetString(element, "id") is not { Length: > 0 } id
            || !element.TryGetProperty("subject", out var subject)
            || !element.TryGetProperty("repository", out var repository))
        {
            return null;
        }

        return new GitHubNotification(
            id,
            GetString(subject, "title") ?? string.Empty,
            GetString(subject, "type") ?? string.Empty,
            GetUri(subject, "url"),
            GetString(repository, "full_name") ?? string.Empty,
            GetUri(repository, "html_url"),
            GetString(element, "reason") ?? string.Empty,
            GetBool(element, "unread"),
            GetDate(element, "updated_at"));
    }

    internal static SubjectDetails ParseSubject(JsonElement element)
    {
        var subjectState = SubjectStateParser.Parse(element);

        var hasPullRequestData = element.TryGetProperty("head", out _) && element.TryGetProperty("base", out _);
        var isPullRequest = hasPullRequestData || element.TryGetProperty("pull_request", out _);
        var pullRequest = hasPullRequestData
            ? GitHubPullRequest.Parse(element, subjectState)
            : null;
        var issue = !isPullRequest && GetInt(element, "number") > 0
            ? IssuesClient.ParseIssue(element)
            : null;
        return new SubjectDetails(subjectState, GetUri(element, "html_url"), pullRequest, issue);
    }
}
