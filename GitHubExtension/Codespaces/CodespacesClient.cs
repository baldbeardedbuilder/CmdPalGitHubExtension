// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

internal interface ICodespacesClient
{
    Task<CodespacesPageResult> GetCodespacesAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken);
}

internal sealed class CodespacesClient(HttpClient httpClient) : ICodespacesClient
{
    internal const int PageSize = 50;

    public async Task<CodespacesPageResult> GetCodespacesAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken)
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to see your codespaces.");
        }

        var uri = page ?? new Uri(account.Host.ApiUrl, $"user/codespaces?per_page={PageSize}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return new CodespacesPageResult(ParseCodespaces(json.RootElement), NextPage(response));
    }

    internal static List<GitHubCodespace> ParseCodespaces(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("codespaces", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a codespaces list we couldn't read.");
        }

        var codespaces = new List<GitHubCodespace>();
        foreach (var element in array.EnumerateArray())
        {
            if (ParseCodespace(element) is { } codespace)
            {
                codespaces.Add(codespace);
            }
        }

        return codespaces;
    }

    internal static GitHubCodespace? ParseCodespace(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetString(element, "name") is not { Length: > 0 } name
            || GetUri(element, "web_url") is not { Scheme: "https" } webUrl
            || !element.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object
            || GetString(repository, "full_name") is not { Length: > 0 } fullName)
        {
            return null;
        }

        var branch = element.TryGetProperty("git_status", out var gitStatus) && gitStatus.ValueKind == JsonValueKind.Object
            ? GetString(gitStatus, "ref")
            : null;

        return new GitHubCodespace(
            name,
            GetString(element, "display_name"),
            fullName,
            branch,
            GetString(element, "state") ?? "Unknown",
            GetDate(element, "last_used_at"),
            webUrl);
    }
}
