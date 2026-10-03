// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly ConditionalWeakTable<HttpResponseMessage, ResponseDiagnosticContext> ResponseContexts = new();

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
        string? timeoutMessage = null,
        bool? isMutation = null,
        DateTimeOffset? ifModifiedSince = null)
    {
        try
        {
            EnsureSameHost(account, uri);
            isMutation ??= IsMutation(method)
                && !await IsReadOnlyGraphQLAsync(method, uri, content, cancellationToken).ConfigureAwait(false);
        }
        catch (GitHubApiException ex)
        {
            using var rejected = OperationDiagnostics.Begin(DiagnosticEvent.RestRequest, verbose: true);
            rejected.Fail(ex, DiagnosticFailure.Authentication, method: method, uri: uri, textSink: logError);
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cancelled = OperationDiagnostics.Begin(DiagnosticEvent.RestRequest, verbose: true);
            cancelled.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            using var preparation = OperationDiagnostics.Begin(DiagnosticEvent.RestRequest, verbose: true);
            preparation.Fail(ex, OperationDiagnostics.Classify(ex), method: method, uri: uri, textSink: logError);
            if (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                var preparationError = new GitHubApiException("The request couldn't be prepared. Try again.", ex);
                OperationDiagnostics.CorrelateFailure(ex, preparationError);
                throw preparationError;
            }

            throw;
        }

        var mutation = isMutation.Value;
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.RestRequest, verbose: !mutation);
        using var request = new HttpRequestMessage(method, uri);
        request.Content = content;
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(UserAgent);
        request.Headers.Add("X-GitHub-Api-Version", apiVersion);
        if (ifModifiedSince is not null)
        {
            if (method != HttpMethod.Get)
            {
                throw new ArgumentException("Conditional refresh must use GET.", nameof(ifModifiedSince));
            }

            request.Headers.IfModifiedSince = ifModifiedSince;
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            operation.Fail(ex, DiagnosticFailure.Transport, method: method, uri: uri, textSink: logError,
                outcome: mutation ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed);
            var apiError = new GitHubApiException(
                mutation
                    ? $"The outcome of the request to {uri.Host} is unknown. Refresh to check before retrying."
                    : $"Couldn't reach {uri.Host}. {ex.Message}",
                ex,
                outcomeUnknown: mutation);
            OperationDiagnostics.CorrelateFailure(ex, apiError);
            throw apiError;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            operation.Fail(ex, DiagnosticFailure.Timeout, method: method, uri: uri, textSink: logError,
                outcome: mutation ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed);
            var apiError = new GitHubApiException(
                timeoutMessage ?? (mutation
                    ? $"The request to {uri.Host} timed out. Its outcome is unknown. Refresh to check before retrying."
                    : $"The request to {uri.Host} timed out. Try again."),
                ex,
                outcomeUnknown: mutation);
            OperationDiagnostics.CorrelateFailure(ex, apiError);
            throw apiError;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            operation.Fail(ex, DiagnosticFailure.Unexpected, method: method, uri: uri, textSink: logError);
            throw;
        }

        ResponseContexts.Add(response, new(operation.Id, mutation));
        if (response.IsSuccessStatusCode
            || response.StatusCode == HttpStatusCode.NotModified && method == HttpMethod.Get && ifModifiedSince is not null)
        {
            operation.Complete(mutation ? DiagnosticOutcome.Accepted : DiagnosticOutcome.Completed,
                (int)response.StatusCode, method, uri);
            return response;
        }

        var error = response.StatusCode == HttpStatusCode.Forbidden && SsoRequired(account, response) is { } sso
            ? sso
            : response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GitHubApiException("GitHub didn't accept your token. Sign out and back in to fix it."),
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => new GitHubApiException("GitHub said no. Your token might be missing a scope, or you hit a rate limit."),
            _ => new GitHubApiException($"{account.Host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                outcomeUnknown: mutation && (int)response.StatusCode >= 500),
        };
        operation.Fail(error, DiagnosticFailure.Http, (int)response.StatusCode, method, uri, logError,
            outcome: error.OutcomeUnknown ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed);
        ResponseContexts.GetValue(response, _ => new(operation.Id)).Failure = error;
        if (!throwOnError)
        {
            return response;
        }

        response.Dispose();
        throw error;
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
            apiVersion: apiVersion, logError: logError, content: content, timeoutMessage: timeoutMessage, isMutation: true).ConfigureAwait(false);
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

            if (ResponseContexts.TryGetValue(response, out var context))
            {
                ResponseContexts.Add(body, context);
            }

            var json = await ReadJsonAsync(body, cancellationToken, logError).ConfigureAwait(false);
            return new GitHubMutationResponse(response.StatusCode, json);
        }
        catch (GitHubApiException ex)
        {
            var error = new GitHubApiException(ex.Message, ex, ex.AuthorizeUrl, outcomeUnknown: true);
            OperationDiagnostics.CorrelateFailure(ex, error);
            throw error;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead,
                operationId: ResponseContexts.TryGetValue(response, out var context) ? context.OperationId : null);
            operation.Fail(ex, DiagnosticFailure.Transport, (int)response.StatusCode, method, uri, logError,
                outcome: DiagnosticOutcome.Unknown);
            var error = new GitHubApiException("GitHub's response was interrupted. Refresh to check before retrying.", ex, outcomeUnknown: true);
            OperationDiagnostics.CorrelateFailure(ex, error);
            throw error;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead,
                operationId: ResponseContexts.TryGetValue(response, out var context) ? context.OperationId : null);
            operation.Fail(ex, DiagnosticFailure.Timeout, (int)response.StatusCode, method, uri, logError,
                outcome: DiagnosticOutcome.Unknown);
            var error = new GitHubApiException("GitHub's response timed out. Refresh to check before retrying.", ex, outcomeUnknown: true);
            OperationDiagnostics.CorrelateFailure(ex, error);
            throw error;
        }
    }

    private static bool IsMutation(HttpMethod method) =>
        method != HttpMethod.Get && method != HttpMethod.Head && method != HttpMethod.Options;

    private static async Task<bool> IsReadOnlyGraphQLAsync(HttpMethod method, Uri uri, HttpContent? content, CancellationToken cancellationToken)
    {
        if (method != HttpMethod.Post || content is null || uri.AbsolutePath is not ("/graphql" or "/api/graphql"))
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (json.RootElement.ValueKind != JsonValueKind.Object || GetString(json.RootElement, "query") is not { } query)
            {
                return false;
            }

            // A document containing any mutation token stays conservative, even when
            // operationName selects a query or the token occurs in a comment/string.
            return Regex.IsMatch(query, @"^\s*(?:query\b|\{)", RegexOptions.CultureInvariant)
                && !Regex.IsMatch(query, @"\bmutation\b", RegexOptions.CultureInvariant);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken, Action<string>? logError = null)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, verbose: true,
            operationId: ResponseContexts.TryGetValue(response, out var context) ? context.OperationId : null);
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            operation.Complete();
            return json;
        }
        catch (JsonException ex)
        {
            operation.Fail(ex, DiagnosticFailure.Schema, (int)response.StatusCode,
                response.RequestMessage?.Method, response.RequestMessage?.RequestUri, logError,
                outcome: context?.Mutation == true ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed);
            var error = new GitHubApiException("GitHub sent back something we couldn't read.", ex,
                outcomeUnknown: context?.Mutation == true);
            OperationDiagnostics.CorrelateFailure(ex, error);
            throw error;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            operation.Fail(ex, OperationDiagnostics.Classify(ex), (int)response.StatusCode,
                response.RequestMessage?.Method, response.RequestMessage?.RequestUri, logError,
                outcome: context?.Mutation == true ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed);
            var error = new GitHubApiException("GitHub sent back something we couldn't read.", ex,
                outcomeUnknown: context?.Mutation == true);
            OperationDiagnostics.CorrelateFailure(ex, error);
            throw error;
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

    internal static string LogEndpoint(Uri? uri) =>
        OperationDiagnostics.RouteTemplate(uri);

    internal static T CorrelateFailure<T>(HttpResponseMessage response, T error)
        where T : Exception
    {
        if (ResponseContexts.TryGetValue(response, out var context) && context.Failure is { } failure)
        {
            OperationDiagnostics.CorrelateFailure(failure, error);
        }

        return error;
    }

    private sealed record ResponseDiagnosticContext(Guid OperationId, bool Mutation = false)
    {
        internal Exception? Failure { get; set; }
    }

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
