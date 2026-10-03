// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal interface IRepositoriesClient
{
    /// <summary>
    /// Gets a page of repos you own, collaborate on, or can see through an org, most recently pushed first.
    /// Pass null for the first page, or the NextPage from a previous result.
    /// </summary>
    Task<RepositoriesPageResult> GetMyRepositoriesAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken);

    /// <summary>
    /// Searches every repo on the host you can see.
    /// </summary>
    Task<IReadOnlyList<GitHubRepository>> SearchAsync(GitHubAccount account, string query, CancellationToken cancellationToken);
}

internal sealed class RepositoriesClient(HttpClient httpClient) : IRepositoriesClient
{
    internal const int PageSize = 50;
    internal const int SearchPageSize = 30;

    public Task<RepositoriesPageResult> GetMyRepositoriesAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Repositories, async () =>
    {
        var uri = page ?? new Uri(
            account.Host.ApiUrl,
            $"user/repos?sort=pushed&per_page={PageSize}&affiliation=owner,collaborator,organization_member");

        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        return new RepositoriesPageResult(ParseRepositories(json.RootElement), NextPage(response));
    }, cancellationToken);

    public Task<IReadOnlyList<GitHubRepository>> SearchAsync(GitHubAccount account, string query, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync<IReadOnlyList<GitHubRepository>>(DiagnosticArea.Repositories, async () =>
    {
        var uri = new Uri(account.Host.ApiUrl, $"search/repositories?q={Uri.EscapeDataString(query)}&per_page={SearchPageSize}");

        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

        return ParseSearch(json.RootElement);
    }, cancellationToken, DiagnosticEvent.PageSearch);

    internal static List<GitHubRepository> ParseSearch(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("items", out var searchItems)
            && searchItems.ValueKind == JsonValueKind.Array
            && root.TryGetProperty("incomplete_results", out var incompleteResult)
            && incompleteResult.ValueKind == JsonValueKind.True)
        {
            throw new GitHubApiException("GitHub returned incomplete repository search results. Try a more specific search.");
        }

        return DomainDiagnostics.Read(DiagnosticArea.Repositories, () =>
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException("GitHub sent back a repository search we couldn't read. Try refreshing.");
            }

            if (root.TryGetProperty("incomplete_results", out var incomplete))
            {
                if (incomplete.ValueKind != JsonValueKind.False)
                {
                    throw new GitHubApiException("GitHub sent back a repository search we couldn't read. Try refreshing.");
                }
            }

            return ParseRepositories(items);
        });
    }

    internal static List<GitHubRepository> ParseRepositories(JsonElement array) =>
        DomainDiagnostics.Read(DiagnosticArea.Repositories, () =>
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a repository list we couldn't read. Try refreshing.");
        }

        var repositories = new List<GitHubRepository>();
        foreach (var element in array.EnumerateArray())
        {
            repositories.Add(ParseRepository(element));
        }

        return repositories;
    });

    internal static GitHubRepository ParseRepository(JsonElement element) =>
        DomainDiagnostics.Read(DiagnosticArea.Repositories, () =>
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetString(element, "full_name") is not { Length: > 0 } fullName
            || string.IsNullOrWhiteSpace(fullName)
            || GetUri(element, "html_url") is not { } webUrl
            || webUrl.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(webUrl.UserInfo))
        {
            throw new GitHubApiException("GitHub sent back a repository we couldn't read. Try refreshing.");
        }

        return new GitHubRepository(
            fullName,
            webUrl,
            GetString(element, "description"),
            GetBool(element, "private"),
            GetBool(element, "fork"),
            GetBool(element, "archived"),
            GetString(element, "language"),
            GetInt(element, "stargazers_count"),
            GetInt(element, "forks_count"),
            GetDate(element, "pushed_at"),
            GetUri(element, "clone_url"));
    });
}
