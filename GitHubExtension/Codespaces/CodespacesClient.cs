// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

internal interface ICodespacesClient
{
    Task<CodespacesPageResult> GetCodespacesAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken);

    Task<GitHubCodespace> StopCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken);

    Task<GitHubCodespace> StartCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken);

    Task<GitHubCodespace> CreateCodespaceAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        CancellationToken cancellationToken);
}

internal sealed class CodespacesClient(HttpClient httpClient) : ICodespacesClient
{
    internal const int PageSize = 50;

    public async Task<GitHubCodespace> StopCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken)
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to close one.");
        }

        var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}/stop");
        const string timeoutMessage = "GitHub took too long to close this codespace. Refresh to check its state, then try again.";
        try
        {
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post, uri, cancellationToken, timeoutMessage: timeoutMessage).ConfigureAwait(false);
            return response.IsAccepted || response.Json is null
                ? await ReadAfterMutationAsync(account, name, cancellationToken).ConfigureAwait(false)
                : ReadMutationCodespace(response.Json.RootElement, name);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(timeoutMessage, ex, outcomeUnknown: true);
        }
    }

    public async Task<GitHubCodespace> StartCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken)
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to start one.");
        }

        var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}/start");
        const string timeoutMessage = "GitHub took too long to start this codespace. Refresh to check its state, then try again.";
        try
        {
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post, uri, cancellationToken, timeoutMessage: timeoutMessage).ConfigureAwait(false);
            return response.IsAccepted || response.Json is null
                ? await ReadAfterMutationAsync(account, name, cancellationToken).ConfigureAwait(false)
                : ReadMutationCodespace(response.Json.RootElement, name);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(timeoutMessage, ex, outcomeUnknown: true);
        }
    }

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

    public async Task<GitHubCodespace> CreateCodespaceAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        CancellationToken cancellationToken)
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to create one.");
        }

        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
        {
            throw new GitHubApiException("Enter a repository as owner/name.");
        }

        var repositoryUri = new Uri(
            account.Host.ApiUrl,
            $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}");
        using var repositoryResponse = await SendAsync(httpClient, account, HttpMethod.Get, repositoryUri, cancellationToken).ConfigureAwait(false);
        using var repositoryJson = await ReadJsonAsync(repositoryResponse, cancellationToken).ConfigureAwait(false);
        if (repositoryJson.RootElement.ValueKind != JsonValueKind.Object
            || !repositoryJson.RootElement.TryGetProperty("id", out var id)
            || !id.TryGetInt64(out var repositoryId)
            || repositoryId <= 0)
        {
            throw new GitHubApiException("GitHub sent back a repository we couldn't read.");
        }

        var requestBody = new Dictionary<string, object?> { ["repository_id"] = repositoryId };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            requestBody["ref"] = branch.Trim();
        }

        using var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var createUri = new Uri(account.Host.ApiUrl, "user/codespaces");
        using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post, createUri, cancellationToken, content: content).ConfigureAwait(false);
        var created = response.Json is null ? null : ParseCodespace(response.Json.RootElement);
        if (created is null)
        {
            throw new GitHubApiException(
                "GitHub received the create request, but didn't identify a Codespace. Check Codespaces on GitHub before creating another.",
                outcomeUnknown: true);
        }

        var authoritative = await ReadAfterMutationAsync(account, created.Name, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(authoritative.RepositoryFullName, repository, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(branch) && !string.Equals(authoritative.Branch, branch.Trim(), StringComparison.Ordinal)))
        {
            throw new GitHubApiException(
                "GitHub created a Codespace, but its repository or branch doesn't match the request. Check Codespaces on GitHub before creating another.",
                outcomeUnknown: true);
        }

        return authoritative;
    }

    private async Task<GitHubCodespace> ReadAfterMutationAsync(GitHubAccount account, string name, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}");
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var codespace = ParseCodespace(json.RootElement);
            return codespace is not null && string.Equals(codespace.Name, name, StringComparison.Ordinal)
                ? codespace
                : throw new GitHubApiException("GitHub sent back a codespace we couldn't verify.", outcomeUnknown: true);
        }
        catch (GitHubApiException ex) when (!ex.OutcomeUnknown)
        {
            throw new GitHubApiException(ex.Message, ex, ex.AuthorizeUrl, outcomeUnknown: true);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException("GitHub took too long to verify the Codespace. Refresh before retrying.", ex, outcomeUnknown: true);
        }
    }

    private static GitHubCodespace ReadMutationCodespace(JsonElement root, string name)
    {
        var codespace = ParseCodespace(root)
            ?? throw new GitHubApiException("GitHub sent back a codespace we couldn't read.", outcomeUnknown: true);
        return string.Equals(codespace.Name, name, StringComparison.Ordinal)
            ? codespace
            : throw new GitHubApiException("GitHub returned a different Codespace. Refresh to verify the requested target.", outcomeUnknown: true);
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
