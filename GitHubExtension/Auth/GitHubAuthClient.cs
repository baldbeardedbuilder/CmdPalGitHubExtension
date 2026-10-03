// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal sealed class GitHubAuthException(string message, Exception? innerException = null) : Exception(message, innerException);

internal interface IGitHubAuthClient
{
    Task<string> ExchangeCodeAsync(GitHubHost host, OAuthOptions options, string code, Uri redirectUri, string codeVerifier, CancellationToken cancellationToken);

    Task<string> GetLoginAsync(GitHubHost host, string token, CancellationToken cancellationToken);
}

internal sealed class GitHubAuthClient(HttpClient httpClient) : IGitHubAuthClient
{
    private static readonly ProductInfoHeaderValue UserAgent = new("BaldBeardedBuilder-CmdPal-GitHub", "1.0");

    public Task<string> ExchangeCodeAsync(GitHubHost host, OAuthOptions options, string code, Uri redirectUri, string codeVerifier, CancellationToken cancellationToken) =>
        AuthDiagnostics.RunAsync(DiagnosticEvent.AuthExchange,
            () => ExchangeCodeCoreAsync(host, options, code, redirectUri, codeVerifier, cancellationToken), cancellationToken);

    private async Task<string> ExchangeCodeCoreAsync(GitHubHost host, OAuthOptions options, string code, Uri redirectUri, string codeVerifier, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(host.WebUrl, "login/oauth/access_token"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId ?? string.Empty,
                ["client_secret"] = options.ClientSecret ?? string.Empty,
                ["code"] = code,
                ["redirect_uri"] = redirectUri.ToString(),
                ["code_verifier"] = codeVerifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(UserAgent);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = json.RootElement;

        if (ReadString(root, "access_token") is { Length: > 0 } accessToken)
        {
            return accessToken;
        }

        var description = ReadString(root, "error_description") ?? ReadString(root, "error");
        throw new GitHubAuthException(description ?? "GitHub didn't return an access token.",
            description is null ? new JsonException() : null);
    }

    public Task<string> GetLoginAsync(GitHubHost host, string token, CancellationToken cancellationToken) =>
        AuthDiagnostics.RunAsync(DiagnosticEvent.AuthIdentity,
            () => GetLoginCoreAsync(host, token, cancellationToken), cancellationToken);

    private async Task<string> GetLoginCoreAsync(GitHubHost host, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(host.ApiUrl, "user"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(UserAgent);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new GitHubAuthException("That token was rejected. Double check it and try again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new GitHubAuthException($"{host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ReadString(json.RootElement, "login") is { Length: > 0 } value
            ? value
            : throw new GitHubAuthException("GitHub didn't tell us who you are.", new JsonException());
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object
            || (root.TryGetProperty(property, out var value)
                && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            throw new GitHubAuthException("GitHub sent back something we couldn't read. Is that the right server URL?",
                new JsonException());
        }

        return root.TryGetProperty(property, out value) ? value.GetString() : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubAuthException($"Couldn't reach {request.RequestUri?.Host}. {ex.Message}", ex);
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
            throw new GitHubAuthException("GitHub sent back something we couldn't read. Is that the right server URL?", ex);
        }
    }
}
