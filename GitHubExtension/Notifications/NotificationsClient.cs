// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

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

    private static readonly ProductInfoHeaderValue UserAgent = new("BaldBeardedBuilder-CmdPal-GitHub", "1.0");

    public async Task<NotificationsPageResult> GetNotificationsAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken)
    {
        var uri = page ?? new Uri(account.Host.ApiUrl, $"notifications?all=true&per_page={PageSize}");
        EnsureSameHost(account, uri);

        using var response = await SendAsync(account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
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

        var next = ParseNextLink(response.Headers.TryGetValues("Link", out var links) ? string.Join(',', links) : null);
        return new NotificationsPageResult(notifications, next);
    }

    public async Task<SubjectDetails?> GetSubjectAsync(GitHubAccount account, Uri subjectApiUrl, CancellationToken cancellationToken)
    {
        EnsureSameHost(account, subjectApiUrl);

        using var response = await SendAsync(account, HttpMethod.Get, subjectApiUrl, cancellationToken, throwOnError: false).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseSubject(json.RootElement);
    }

    public async Task MarkAsReadAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}");
        using var response = await SendAsync(account, HttpMethod.Patch, uri, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkAsDoneAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken)
    {
        var uri = new Uri(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}");
        using var response = await SendAsync(account, HttpMethod.Delete, uri, cancellationToken).ConfigureAwait(false);
    }

    internal static GitHubNotification? ParseNotification(JsonElement element)
    {
        if (GetString(element, "id") is not { Length: > 0 } id
            || !element.TryGetProperty("subject", out var subject)
            || !element.TryGetProperty("repository", out var repository))
        {
            return null;
        }

        var updatedAt = GetString(element, "updated_at") is { } updated
            && DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

        return new GitHubNotification(
            id,
            GetString(subject, "title") ?? string.Empty,
            GetString(subject, "type") ?? string.Empty,
            GetUri(subject, "url"),
            GetString(repository, "full_name") ?? string.Empty,
            GetUri(repository, "html_url"),
            GetString(element, "reason") ?? string.Empty,
            element.TryGetProperty("unread", out var unread) && unread.ValueKind == JsonValueKind.True,
            updatedAt);
    }

    internal static SubjectDetails ParseSubject(JsonElement element)
    {
        var state = GetString(element, "state");
        var merged = (element.TryGetProperty("merged", out var m) && m.ValueKind == JsonValueKind.True)
            || GetString(element, "merged_at") is not null;
        var draft = element.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;

        var subjectState = state switch
        {
            _ when merged => SubjectState.Merged,
            "open" when draft => SubjectState.Draft,
            "open" => SubjectState.Open,
            "closed" when GetString(element, "state_reason") == "not_planned" => SubjectState.NotPlanned,
            "closed" => SubjectState.Closed,
            _ => SubjectState.Unknown,
        };

        return new SubjectDetails(subjectState, GetUri(element, "html_url"));
    }

    /// <summary>
    /// Pulls the rel="next" url out of a GitHub Link header.
    /// </summary>
    internal static Uri? ParseNextLink(string? linkHeader)
    {
        if (string.IsNullOrEmpty(linkHeader))
        {
            return null;
        }

        foreach (var part in linkHeader.Split(','))
        {
            var sections = part.Split(';');
            if (sections.Length < 2 || !sections.Skip(1).Any(s => s.Trim() == "rel=\"next\""))
            {
                continue;
            }

            var url = sections[0].Trim().TrimStart('<').TrimEnd('>');
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
        }

        return null;
    }

    private static void EnsureSameHost(GitHubAccount account, Uri uri)
    {
        // We attach the user's token to these requests, so never follow a url off of their API host.
        if (!string.Equals(uri.Authority, account.Host.ApiUrl.Authority, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitHubApiException($"Refusing to send your token to {uri.Authority}.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(GitHubAccount account, HttpMethod method, Uri uri, CancellationToken cancellationToken, bool throwOnError = true)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException($"Couldn't reach {uri.Host}. {ex.Message}", ex);
        }

        if (!throwOnError || response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new GitHubApiException("GitHub didn't accept your token. Sign out and back in to fix it."),
                HttpStatusCode.Forbidden => new GitHubApiException("GitHub said no. Your token might be missing the notifications scope, or you hit a rate limit."),
                _ => new GitHubApiException($"{account.Host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}."),
            };
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new GitHubApiException("GitHub sent back something we couldn't read.", ex);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri? GetUri(JsonElement element, string name) =>
        GetString(element, name) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
}
