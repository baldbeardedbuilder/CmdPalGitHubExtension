// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics.CodeAnalysis;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

/// <summary>
/// A GitHub server the user can sign in to: github.com, a GHE.com tenant, or a GitHub Enterprise Server.
/// </summary>
internal sealed record GitHubHost
{
    public static GitHubHost GitHubDotCom { get; } = new(new Uri("https://github.com/"));

    private GitHubHost(Uri webUrl)
    {
        WebUrl = webUrl;
        ApiUrl = BuildApiUrl(webUrl);
    }

    public Uri WebUrl { get; }

    public Uri ApiUrl { get; }

    public string Name => WebUrl.Authority;

    public bool IsGitHubDotCom => string.Equals(WebUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses what a user typed, like "github.example.com" or "https://github.example.com/", into a host.
    /// Only https is allowed because we send tokens to it.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out GitHubHost? host)
    {
        host = null;

        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var normalized = new UriBuilder(Uri.UriSchemeHttps, uri.Host.ToLowerInvariant(), uri.IsDefaultPort ? -1 : uri.Port, "/").Uri;
        host = string.Equals(normalized.Host, "github.com", StringComparison.Ordinal) && normalized.IsDefaultPort
            ? GitHubDotCom
            : new GitHubHost(normalized);
        return true;
    }

    public bool Equals(GitHubHost? other) => other is not null && WebUrl == other.WebUrl;

    public override int GetHashCode() => WebUrl.GetHashCode();

    public override string ToString() => Name;

    private static Uri BuildApiUrl(Uri webUrl)
    {
        if (string.Equals(webUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri("https://api.github.com/");
        }

        // GHE.com tenants (data residency) host the API on a subdomain, like api.octocorp.ghe.com.
        if (webUrl.Host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri($"https://api.{webUrl.Authority}/");
        }

        return new Uri(webUrl, "api/v3/");
    }
}
