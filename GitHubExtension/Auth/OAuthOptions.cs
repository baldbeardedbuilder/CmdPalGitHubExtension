// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Reflection;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

/// <summary>
/// The github.com OAuth app credentials. They're baked in at build time (see CONTRIBUTING.md) and never committed.
/// </summary>
internal sealed record OAuthOptions(string? ClientId, string? ClientSecret)
{
    public const string Scopes = "repo read:org notifications codespace";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    public static OAuthOptions FromAssembly(Assembly? assembly = null)
    {
        var metadata = (assembly ?? typeof(OAuthOptions).Assembly).GetCustomAttributes<AssemblyMetadataAttribute>().ToList();
        string? Get(string key) => metadata.FirstOrDefault(m => m.Key == key)?.Value;
        return new OAuthOptions(Get("GitHubOAuthClientId"), Get("GitHubOAuthClientSecret"));
    }
}
