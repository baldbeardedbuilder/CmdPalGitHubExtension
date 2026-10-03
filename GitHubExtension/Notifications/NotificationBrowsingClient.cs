using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal sealed record NotificationQuery(bool UnreadOnly = false, bool Participating = false, string? Repository = null,
    DateTimeOffset? Since = null, DateTimeOffset? Before = null);
internal sealed record NotificationPollResult(IReadOnlyList<GitHubNotification> Notifications, Uri? NextPage,
    bool NotModified, DateTimeOffset? LastModified, TimeSpan PollInterval);
internal sealed record BulkNotificationReadRequest(
    [property: JsonPropertyName("last_read_at")] string LastReadAt,
    [property: JsonPropertyName("read")] bool Read = true);

[JsonSerializable(typeof(BulkNotificationReadRequest))]
internal sealed partial class NotificationBrowsingJsonContext : JsonSerializerContext;

internal interface INotificationBrowsingClient
{
    Task<NotificationPollResult> GetNotificationsAsync(GitHubAccount account, NotificationQuery query, Uri? page,
        DateTimeOffset? modifiedSince, CancellationToken token);
    Task<bool> MarkAllReadAsync(GitHubAccount account, string? repository, DateTimeOffset cutoff, CancellationToken token);
}

internal sealed partial class NotificationsClient : INotificationBrowsingClient
{
    public Task<NotificationPollResult> GetNotificationsAsync(GitHubAccount account, NotificationQuery query, Uri? page,
        DateTimeOffset? modifiedSince, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Notifications, async () =>
        {
            var first = QueryUri(account, query);
            var uri = page ?? first;
            ValidateQueryPage(uri, first);
            var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token,
                throwOnError: false, ifModifiedSince: page is null ? modifiedSince : null).ConfigureAwait(false);

            using (response)
            {
                var interval = response.Headers.TryGetValues("X-Poll-Interval", out var values)
                    && int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                    ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(60);
                var modified = response.Content.Headers.LastModified;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (page is not null || modifiedSince is null) { throw new GitHubApiException("GitHub returned an unexpected conditional response."); }
                    return new NotificationPollResult([], null, true, modified ?? modifiedSince, interval);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var error = SsoRequired(account, response) ?? new GitHubApiException(
                        response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests
                        ? "Couldn't read notifications. Use a classic PAT with notifications or repo scope, or check your existing OAuth authorization and rate limit."
                        : "GitHub couldn't return your notifications. Try refreshing later.");
                    error.Data["NotificationPollInterval"] = interval;
                    throw CorrelateFailure(response, error);
                }

                using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
                var next = NextPage(response);
                if (next is not null) { ValidateQueryPage(next, first); }
                return new NotificationPollResult(ParseNotifications(json.RootElement), next, false, modified, interval);
            }
        }, cancellationToken: token);

    public async Task<bool> MarkAllReadAsync(GitHubAccount account, string? repository, DateTimeOffset cutoff, CancellationToken token)
    {
        var query = new NotificationQuery(Repository: repository);
        var uri = new Uri(QueryUri(account, query).GetLeftPart(UriPartial.Path));
        using var content = new StringContent(JsonSerializer.Serialize(new BulkNotificationReadRequest(cutoff.ToUniversalTime().ToString("O")),
            NotificationBrowsingJsonContext.Default.BulkNotificationReadRequest), Encoding.UTF8, "application/json");
        using var response = await SendAsync(httpClient, account, HttpMethod.Put, uri, token, content: content, throwOnError: false).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotModified)
        {
            throw SsoRequired(account, response) ?? new GitHubApiException(
                "Couldn't mark this scope read. Check your classic PAT's notifications or repo scope, OAuth authorization, and rate limit.",
                outcomeUnknown: (int)response.StatusCode >= 500);
        }

        return response.StatusCode == HttpStatusCode.Accepted;
    }

    internal static Uri QueryUri(GitHubAccount account, NotificationQuery query)
    {
        if (query.Since > query.Before) { throw new GitHubApiException("The since timestamp must come before the before timestamp."); }
        var path = "notifications";
        if (!string.IsNullOrWhiteSpace(query.Repository))
        {
            _ = RepositoriesClient.WatchingUri(account, query.Repository);
            path = $"repos/{string.Join('/', query.Repository.Split('/').Select(Uri.EscapeDataString))}/notifications";
        }

        return new(account.Host.ApiUrl, $"{path}?all={(!query.UnreadOnly).ToString().ToLowerInvariant()}&participating={query.Participating.ToString().ToLowerInvariant()}&per_page={PageSize}"
            + (query.Since is { } since ? $"&since={Uri.EscapeDataString(since.ToUniversalTime().ToString("O"))}" : "")
            + (query.Before is { } before ? $"&before={Uri.EscapeDataString(before.ToUniversalTime().ToString("O"))}" : ""));
    }

    internal static void ValidateQueryPage(Uri page, Uri first)
    {
        var expected = System.Web.HttpUtility.ParseQueryString(first.Query);
        var actual = System.Web.HttpUtility.ParseQueryString(page.Query);
        if (page.GetLeftPart(UriPartial.Path) != first.GetLeftPart(UriPartial.Path)
            || !string.IsNullOrEmpty(page.UserInfo) || !string.IsNullOrEmpty(page.Fragment)
            || expected.AllKeys.Any(key => expected[key] != actual[key])
            || actual.AllKeys.Any(key => key != "page" && !expected.AllKeys.Contains(key))
            || actual["page"] is { } number && (!int.TryParse(number, out var n) || n < 1))
        {
            throw new GitHubApiException("GitHub returned a notification page outside your selected scope.");
        }
    }
}
