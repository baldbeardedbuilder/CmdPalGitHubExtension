// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

internal interface ICodespacesClient
{
    Task<GitHubCodespace> GetCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken);

    Task DeleteCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken);

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

    public Task<GitHubCodespace> GetCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Codespaces, async () =>
    {
        RequireGitHubDotCom(account);
        var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return DomainDiagnostics.Read(DiagnosticArea.Codespaces, () =>
        {
            var codespace = ReadCodespace(json.RootElement);
            return codespace.Name == name
                ? codespace
                : throw new GitHubApiException("GitHub sent back a codespace we couldn't read.");
        });
    }, cancellationToken: cancellationToken);

    public async Task DeleteCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken)
    {
        RequireGitHubDotCom(account);
        var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Delete, uri, cancellationToken,
            timeoutMessage: "GitHub took too long to delete this codespace. Refresh to check whether it still exists.").ConfigureAwait(false);
    }

    private static void RequireGitHubDotCom(GitHubAccount account)
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com.");
        }
    }

    public async Task<GitHubCodespace> StopCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken)
    {
        var sent = false;
        return await DomainDiagnostics.RunAsync(DiagnosticArea.Codespaces, async () =>
        {
            if (!account.Host.IsGitHubDotCom)
            {
                throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to close one.");
            }

            var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}/stop");
            const string timeoutMessage = "GitHub took too long to close this codespace. Refresh to check its state, then try again.";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sent = true;
                using var response = await SendAsync(httpClient, account, HttpMethod.Post, uri, cancellationToken, timeoutMessage: timeoutMessage).ConfigureAwait(false);
                using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                return ReadCodespace(json.RootElement);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GitHubApiException(timeoutMessage, ex);
            }
        }, DiagnosticEvent.CodespaceStop,
            space => MutationOutcome(space, "Shutdown"),
            () => sent, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubCodespace> StartCodespaceAsync(GitHubAccount account, string name, CancellationToken cancellationToken)
    {
        var sent = false;
        return await DomainDiagnostics.RunAsync(DiagnosticArea.Codespaces, async () =>
        {
            if (!account.Host.IsGitHubDotCom)
            {
                throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to start one.");
            }

            var uri = new Uri(account.Host.ApiUrl, $"user/codespaces/{Uri.EscapeDataString(name)}/start");
            const string timeoutMessage = "GitHub took too long to start this codespace. Refresh to check its state, then try again.";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                sent = true;
                using var response = await SendAsync(httpClient, account, HttpMethod.Post, uri, cancellationToken, timeoutMessage: timeoutMessage).ConfigureAwait(false);
                using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                return ReadCodespace(json.RootElement);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GitHubApiException(timeoutMessage, ex);
            }
        }, DiagnosticEvent.CodespaceStart,
            space => MutationOutcome(space, "Available"),
            () => sent, cancellationToken).ConfigureAwait(false);
    }

    public Task<CodespacesPageResult> GetCodespacesAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Codespaces, async () =>
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to see your codespaces.");
        }

        var uri = page ?? new Uri(account.Host.ApiUrl, $"user/codespaces?per_page={PageSize}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var codespaces = ParseCodespaces(json.RootElement);
        int? totalCount = null;
        var complete = codespaces.Count == json.RootElement.GetProperty("codespaces").GetArrayLength();
        if (json.RootElement.TryGetProperty("total_count", out var total))
        {
            if (total.ValueKind == JsonValueKind.Number && total.TryGetInt32(out var count) && count >= 0)
            {
                totalCount = count;
            }
            else
            {
                complete = false;
            }
        }

        return new CodespacesPageResult(codespaces, NextPage(response),
            complete, totalCount);
    }, cancellationToken: cancellationToken);

    public async Task<GitHubCodespace> CreateCodespaceAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        CancellationToken cancellationToken)
    {
        var sent = false;
        return await DomainDiagnostics.RunAsync(DiagnosticArea.Codespaces, async () =>
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
            var repositoryId = DomainDiagnostics.Read(DiagnosticArea.Codespaces, () =>
            {
                if (repositoryJson.RootElement.ValueKind != JsonValueKind.Object
                    || !repositoryJson.RootElement.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number
                    || !id.TryGetInt64(out var value)
                    || value <= 0)
                {
                    throw new GitHubApiException("GitHub sent back a repository we couldn't read.");
                }

                return value;
            });

            var requestBody = new CreateCodespaceRequest(repositoryId, GitHubJson.Optional(branch));
            using var content = new StringContent(
                JsonSerializer.Serialize(requestBody, GitHubJsonContext.Default.CreateCodespaceRequest),
                Encoding.UTF8, "application/json");
            var createUri = new Uri(account.Host.ApiUrl, "user/codespaces");
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            using var response = await SendAsync(httpClient, account, HttpMethod.Post, createUri, cancellationToken, content: content).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ReadCodespace(json.RootElement);
        }, DiagnosticEvent.CodespaceCreate,
            space => MutationOutcome(space, "Available"),
            () => sent, cancellationToken).ConfigureAwait(false);
    }

    private static GitHubCodespace ReadCodespace(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Codespaces, () => ParseCodespaceCore(root, reportFailure: false)
            ?? throw new GitHubApiException("GitHub sent back a codespace we couldn't read."));

    private static DiagnosticOutcome MutationOutcome(GitHubCodespace space, string completedState) =>
        space.State == completedState ? DiagnosticOutcome.Completed
            : space.State == "Failed" ? DiagnosticOutcome.Failed : DiagnosticOutcome.Accepted;

    internal static List<GitHubCodespace> ParseCodespaces(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Codespaces, () =>
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
    });

    internal static GitHubCodespace? ParseCodespace(JsonElement element) => ParseCodespaceCore(element, reportFailure: true);

    private static GitHubCodespace? ParseCodespaceCore(JsonElement element, bool reportFailure)
    {
        if (element.ValueKind != JsonValueKind.Object
            || GetString(element, "name") is not { Length: > 0 } name
            || GetUri(element, "web_url") is not { Scheme: "https" } webUrl
            || !element.TryGetProperty("repository", out var repository)
            || repository.ValueKind != JsonValueKind.Object
            || GetString(repository, "full_name") is not { Length: > 0 } fullName)
        {
            if (reportFailure)
            {
                DomainDiagnostics.InvalidEntry(DiagnosticArea.Codespaces);
            }
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
            webUrl,
            ReadGitStatusBoolean(gitStatus, "has_uncommitted_changes"),
            ReadGitStatusBoolean(gitStatus, "has_unpushed_changes"),
            ReadGitStatusCount(gitStatus, "ahead"),
            ReadGitStatusCount(gitStatus, "behind"));
    }

    private static bool? ReadGitStatusBoolean(JsonElement status, string name) =>
        status.ValueKind == JsonValueKind.Object && status.TryGetProperty(name, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? ReadGitStatusCount(JsonElement status, string name) =>
        status.ValueKind == JsonValueKind.Object && status.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0 ? count : null;
}
