// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Api;

internal sealed class GitHubApiException(string message, Exception? innerException = null, Uri? authorizeUrl = null, bool outcomeUnknown = false) : Exception(message, innerException)
{
    /// <summary>
    /// Where the user can grant this app SAML SSO access to the organization that blocked the request.
    /// </summary>
    public Uri? AuthorizeUrl { get; } = authorizeUrl;

    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

internal sealed partial class GitHubMutationResponse(HttpStatusCode statusCode, JsonDocument? json) : IDisposable
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public bool IsAccepted => StatusCode == HttpStatusCode.Accepted;

    public JsonDocument? Json { get; } = json;

    public void Dispose() => Json?.Dispose();
}

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
        HttpContent? content = null,
        string? timeoutMessage = null)
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
            throw new GitHubApiException(
                IsMutation(method)
                    ? $"The outcome of the request to {uri.Host} is unknown. Refresh to check before retrying."
                    : $"Couldn't reach {uri.Host}. {ex.Message}",
                ex,
                outcomeUnknown: IsMutation(method));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logError($"GitHub API error: {method} {LogEndpoint(uri)}; transport=Timeout.");
            throw new GitHubApiException(
                timeoutMessage ?? (IsMutation(method)
                    ? $"The request to {uri.Host} timed out. Its outcome is unknown. Refresh to check before retrying."
                    : $"The request to {uri.Host} timed out. Try again."),
                ex,
                outcomeUnknown: IsMutation(method));
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
            if (response.StatusCode == HttpStatusCode.Forbidden && SsoRequired(account, response) is { } sso)
            {
                throw sso;
            }

            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new GitHubApiException("GitHub didn't accept your token. Sign out and back in to fix it."),
                HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new GitHubApiException("GitHub said no. Your token might be missing a scope, or you hit a rate limit."),
                _ => new GitHubApiException(
                    $"{account.Host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                    outcomeUnknown: IsMutation(method) && (int)response.StatusCode >= 500),
            };
        }
    }

    public static async Task<GitHubMutationResponse> SendMutationAsync(
        HttpClient httpClient,
        GitHubAccount account,
        HttpMethod method,
        Uri uri,
        CancellationToken cancellationToken,
        string apiVersion = "2022-11-28",
        Action<string>? logError = null,
        HttpContent? content = null,
        string? timeoutMessage = null)
    {
        if (!IsMutation(method))
        {
            throw new ArgumentException("A mutation must use a write method.", nameof(method));
        }

        using var response = await SendAsync(
            httpClient, account, method, uri, cancellationToken,
            apiVersion: apiVersion, logError: logError, content: content, timeoutMessage: timeoutMessage).ConfigureAwait(false);
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return new GitHubMutationResponse(response.StatusCode, null);
            }

            using var body = new HttpResponseMessage(response.StatusCode)
            {
                Content = new ByteArrayContent(bytes),
                RequestMessage = response.RequestMessage,
            };
            foreach (var header in response.Headers)
            {
                body.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            var json = await ReadJsonAsync(body, cancellationToken, logError).ConfigureAwait(false);
            return new GitHubMutationResponse(response.StatusCode, json);
        }
        catch (GitHubApiException ex)
        {
            throw new GitHubApiException(ex.Message, ex, ex.AuthorizeUrl, outcomeUnknown: true);
        }
        catch (HttpRequestException ex)
        {
            (logError ?? LogError)($"GitHub API error: {method} {LogEndpoint(uri)}; transport={ex.HttpRequestError}.");
            throw new GitHubApiException("GitHub's response was interrupted. Refresh to check before retrying.", ex, outcomeUnknown: true);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            (logError ?? LogError)($"GitHub API error: {method} {LogEndpoint(uri)}; transport=Timeout.");
            throw new GitHubApiException("GitHub's response timed out. Refresh to check before retrying.", ex, outcomeUnknown: true);
        }
    }

    private static bool IsMutation(HttpMethod method) =>
        method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

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

    /// <summary>
    /// GitHub answers with "X-GitHub-SSO: required; url=..." when an org's SAML SSO hasn't been granted to this token.
    /// </summary>
    internal static GitHubApiException? SsoRequired(GitHubAccount account, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-GitHub-SSO", out var values))
        {
            return null;
        }

        var parts = string.Join(';', values).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!parts.Contains("required", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var url = parts
            .Where(p => p.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
            .Select(p => Uri.TryCreate(p[4..], UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(uri => uri is not null
                && uri.Scheme == Uri.UriSchemeHttps
                && string.IsNullOrEmpty(uri.UserInfo)
                && string.Equals(uri.Authority, account.Host.WebUrl.Authority, StringComparison.OrdinalIgnoreCase));

        var segments = url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var org = segments.Length >= 2 && segments[0] == "orgs" ? Uri.UnescapeDataString(segments[1]) : null;
        var message = org is null
            ? "An organization requires SAML single sign-on. Authorize this app for it on GitHub, then refresh."
            : $"The {org} organization requires SAML single sign-on. Authorize this app for {org}, then refresh.";
        return new GitHubApiException(message, authorizeUrl: url);
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
