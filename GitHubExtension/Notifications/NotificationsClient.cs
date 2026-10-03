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

        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a notification list we couldn't read. Try refreshing.");
        }

        var notifications = new List<GitHubNotification>();
        foreach (var element in json.RootElement.EnumerateArray())
        {
            notifications.Add(ParseNotification(element));
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
        using var response = await SendMutationAsync(httpClient, account, HttpMethod.Patch, uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkAsDoneAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}");
        using var response = await SendMutationAsync(httpClient, account, HttpMethod.Delete, uri, cancellationToken).ConfigureAwait(false);
    }

    internal static GitHubNotification ParseNotification(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetString(element, "id") is not { Length: > 0 } id
            || string.IsNullOrWhiteSpace(id)
            || !element.TryGetProperty("subject", out var subject)
            || subject.ValueKind != JsonValueKind.Object
            || GetString(subject, "title") is not { Length: > 0 } title
            || string.IsNullOrWhiteSpace(title)
            || GetString(subject, "type") is not { Length: > 0 } type
            || string.IsNullOrWhiteSpace(type)
            || !element.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object
            || GetString(repository, "full_name") is not { Length: > 0 } fullName
            || string.IsNullOrWhiteSpace(fullName)
            || GetUri(repository, "html_url") is not { } webUrl
            || webUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(webUrl.UserInfo)
            || GetString(element, "reason") is not { Length: > 0 } reason
            || string.IsNullOrWhiteSpace(reason)
            || !element.TryGetProperty("unread", out var unread)
            || unread.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || GetDate(element, "updated_at") == DateTimeOffset.MinValue)
        {
            throw new GitHubApiException("GitHub sent back a notification we couldn't read. Try refreshing.");
        }

        var subjectApiUrl = GetUri(subject, "url");
        if (subject.TryGetProperty("url", out var url) && url.ValueKind != JsonValueKind.Null
            && (subjectApiUrl is null || subjectApiUrl.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(subjectApiUrl.UserInfo)))
        {
            throw new GitHubApiException("GitHub sent back a notification we couldn't read. Try refreshing.");
        }

        return new GitHubNotification(
            id,
            title,
            type,
            subjectApiUrl,
            fullName,
            webUrl,
            reason,
            unread.ValueKind == JsonValueKind.True,
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
