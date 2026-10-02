// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Api;

internal sealed class GitHubApiException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// The plumbing every GitHub REST call needs: auth headers, friendly errors, JSON, and pagination links.
/// </summary>
internal static class GitHubRest
{
    private static readonly ProductInfoHeaderValue UserAgent = new("BaldBeardedBuilder-CmdPal-GitHub", "1.0");

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        GitHubAccount account,
        HttpMethod method,
        Uri uri,
        CancellationToken cancellationToken,
        bool throwOnError = true,
        string apiVersion = "2022-11-28",
        Action<string>? logError = null,
        HttpContent? content = null)
    {
        logError ??= LogError;
        EnsureSameHost(account, uri);

        using var request = new HttpRequestMessage(method, uri);
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", apiVersion);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logError($"GitHub API error: {method} {LogEndpoint(uri)}; transport={ex.HttpRequestError}.");
            throw new GitHubApiException($"Couldn't reach {uri.Host}. {ex.Message}", ex);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logError($"GitHub API error: {method} {LogEndpoint(uri)}; transport=Timeout.");
            throw;
        }

        if (!response.IsSuccessStatusCode)
        {
            logError($"GitHub API error: {method} {LogEndpoint(uri)}; status={(int)response.StatusCode}; "
                + $"request-id={Header(response, "X-GitHub-Request-Id")}; "
                + $"rate-limit-remaining={Header(response, "X-RateLimit-Remaining")}; "
                + $"rate-limit-reset={Header(response, "X-RateLimit-Reset")}; "
                + $"sso-header-present={response.Headers.Contains("X-GitHub-SSO")}.");
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
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new GitHubApiException("GitHub said no. Your token might be missing a scope, or you hit a rate limit."),
                _ => new GitHubApiException($"{account.Host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}."),
            };
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken, Action<string>? logError = null)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            (logError ?? LogError)(
                $"GitHub API error: invalid JSON; endpoint={LogEndpoint(response.RequestMessage?.RequestUri)}; "
                + $"status={(int)response.StatusCode}; request-id={Header(response, "X-GitHub-Request-Id")}.");
            throw new GitHubApiException("GitHub sent back something we couldn't read.", ex);
        }
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(',', values) : "unknown";

    internal static string LogEndpoint(Uri? uri) =>
        uri is null ? "unknown" : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}";

    internal static void LogError(string message) =>
        ExtensionHost.LogMessage(new LogMessage(message) { State = MessageState.Error });

    public static Uri? NextPage(HttpResponseMessage response) =>
        ParseNextLink(response.Headers.TryGetValues("Link", out var links) ? string.Join(',', links) : null);

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

    public static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static Uri? GetUri(JsonElement element, string name) =>
        GetString(element, name) is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;

    public static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    public static DateTimeOffset GetDate(JsonElement element, string name) =>
        GetString(element, name) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

    private static void EnsureSameHost(GitHubAccount account, Uri uri)
    {
        // We attach the user's token to these requests, so never follow a url off of their API host.
        if (!string.Equals(uri.Authority, account.Host.ApiUrl.Authority, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new GitHubApiException($"Refusing to send your token to {uri.Authority}.");
        }
    }
}
