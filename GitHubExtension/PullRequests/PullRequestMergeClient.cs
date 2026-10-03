// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal sealed record PullRequestMergeTarget(string Repository, int Number, string BaseRef, string HeadSha, string[] Methods, string? StackScope = null);

internal sealed record PullRequestMergeResult(string Status, string? Uuid, string Summary);

internal interface IPullRequestMergeClient
{
    Task<PullRequestMergeTarget> GetTargetAsync(GitHubAccount account, string repository, int number, CancellationToken cancellationToken);

    Task<PullRequestMergeResult> MergeAsync(GitHubAccount account, PullRequestMergeTarget target, string method, CancellationToken cancellationToken);

    Task<PullRequestMergeResult> GetStatusAsync(GitHubAccount account, PullRequestMergeTarget target, string uuid, CancellationToken cancellationToken);
}

internal sealed class PullRequestMergeClient(HttpClient httpClient) : IPullRequestMergeClient
{
    public async Task<PullRequestMergeTarget> GetTargetAsync(
        GitHubAccount account, string repository, int number, CancellationToken cancellationToken)
    {
        EnsureSupport(account);
        var repoUri = RepositoryUri(account, repository);
        using var repoResponse = await SendAsync(httpClient, account, HttpMethod.Get, repoUri, cancellationToken).ConfigureAwait(false);
        using var repoJson = await ReadJsonAsync(repoResponse, cancellationToken).ConfigureAwait(false);
        var repo = repoJson.RootElement;
        if (repo.ValueKind != JsonValueKind.Object
            || !repo.TryGetProperty("permissions", out var permissions)
            || permissions.ValueKind != JsonValueKind.Object
            || !GetBool(permissions, "push"))
        {
            throw new GitHubApiException("Write access to this repository is required to merge.");
        }

        var methods = new List<string>();
        if (GetBool(repo, "allow_merge_commit")) methods.Add("merge");
        if (GetBool(repo, "allow_squash_merge")) methods.Add("squash");
        if (GetBool(repo, "allow_rebase_merge")) methods.Add("rebase");
        if (methods.Count == 0)
        {
            throw new GitHubApiException("No supported merge methods are enabled for this repository.");
        }

        using var response = await SendAsync(
            httpClient, account, HttpMethod.Get, PullUri(account, repository, number), cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var pull = json.RootElement;
        if (pull.ValueKind != JsonValueKind.Object || GetString(pull, "state") != "open" || GetBool(pull, "draft"))
        {
            throw new GitHubApiException("Only open, non-draft pull requests can be merged.");
        }

        string? stackScope = null;
        if (pull.TryGetProperty("stack", out var stack) && stack.ValueKind != JsonValueKind.Null)
        {
            if (stack.ValueKind != JsonValueKind.Object)
            {
                throw new GitHubApiException("GitHub returned a stack scope we could not confirm. Merge on GitHub instead.");
            }

            stackScope = stack.GetRawText();
        }

        if (pull.TryGetProperty("mergeable", out var mergeable) && mergeable.ValueKind == JsonValueKind.False)
        {
            throw new GitHubApiException("This pull request has merge conflicts. Resolve them on GitHub first.");
        }

        if (!pull.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object
            || GetString(head, "sha") is not { Length: > 0 } sha
            || !pull.TryGetProperty("base", out var target) || target.ValueKind != JsonValueKind.Object
            || GetString(target, "ref") is not { Length: > 0 } baseRef)
        {
            throw new GitHubApiException("GitHub did not provide a head SHA and target branch to confirm.");
        }

        return new(repository, number, baseRef, sha, [.. methods], stackScope);
    }

    public async Task<PullRequestMergeResult> MergeAsync(
        GitHubAccount account, PullRequestMergeTarget target, string method, CancellationToken cancellationToken)
    {
        if (!target.Methods.Contains(method, StringComparer.Ordinal) || method is not ("merge" or "squash" or "rebase"))
        {
            throw new GitHubApiException("Choose an enabled merge method.");
        }

        // Recheck the confirmed scope immediately before sending the mutation.
        var current = await GetTargetAsync(account, target.Repository, target.Number, cancellationToken).ConfigureAwait(false);
        if (current.HeadSha != target.HeadSha || current.BaseRef != target.BaseRef
            || current.StackScope != target.StackScope || !current.Methods.Contains(method))
        {
            throw new GitHubApiException("The head SHA, target branch, stack scope, or allowed methods changed. Load a fresh confirmation before merging.");
        }

        using var content = new StringContent(
            $$"""{"sha":{{JsonSerializer.Serialize(target.HeadSha)}},"merge_method":{{JsonSerializer.Serialize(method)}},"merge_action":"default","bypass_rules":false}""",
            Encoding.UTF8, "application/json");
        using var response = await SendAsync(
            httpClient, account, HttpMethod.Put, new Uri(PullUri(account, target.Repository, target.Number).AbsoluteUri + "/merge-async"),
            cancellationToken, throwOnError: false, content: content,
            timeoutMessage: "The merge request timed out. Its outcome is unknown. Check the PR on GitHub before trying again.").ConfigureAwait(false);
        EnsureResultResponse(account, response, submission: true, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResult(json.RootElement, response.StatusCode == HttpStatusCode.Conflict);
    }

    public async Task<PullRequestMergeResult> GetStatusAsync(
        GitHubAccount account, PullRequestMergeTarget target, string uuid, CancellationToken cancellationToken)
    {
        EnsureSupport(account);
        if (!Guid.TryParse(uuid, out _))
        {
            throw new GitHubApiException("GitHub did not return a valid merge request ID.");
        }

        var uri = new Uri(PullUri(account, target.Repository, target.Number).AbsoluteUri + $"/merge-async/{Uri.EscapeDataString(uuid)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken, throwOnError: false).ConfigureAwait(false);
        EnsureResultResponse(account, response, submission: false, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseResult(json.RootElement);
    }

    internal static PullRequestMergeResult ParseResult(JsonElement root, bool existingRequest = false)
    {
        var status = root.ValueKind == JsonValueKind.Object ? GetString(root, "status") : null;
        if (root.ValueKind != JsonValueKind.Object
            || status is not ("pending" or "enqueued" or "failed" or "merged")
            || !root.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubApiException("GitHub returned an unknown merge result. Check the PR on GitHub.");
        }

        string? uuid = null;
        var summary = status switch
        {
            "merged" => "Merged. GitHub confirmed the merge completed.",
            "enqueued" => "Enqueued, not merged. Follow the merge queue on GitHub for the final outcome.",
            "failed" => "Failed. GitHub did not complete this merge request. Check rules, checks, permissions, and conflicts on GitHub.",
            _ => "Pending. GitHub is still processing the merge request.",
        };
        if (status == "pending")
        {
            uuid = GetString(details, "uuid");
            if (!Guid.TryParse(uuid, out _)
                || GetString(details, "expected_head_sha") is not { Length: > 0 } sha
                || GetString(details, "merge_method") is not { Length: > 0 } method
                || GetString(details, "merge_action") is not { Length: > 0 } action)
            {
                throw new GitHubApiException("GitHub returned an incomplete pending result. Check the PR on GitHub.");
            }

            summary += $" Head SHA: {sha}. Method: {method}. Action: {action}. Rules bypass: {GetBool(details, "bypass_rules")}.";
        }

        if (status == "merged" && GetString(details, "sha") is not { Length: > 0 })
        {
            throw new GitHubApiException("GitHub did not confirm a merge commit. Check the PR on GitHub.");
        }

        if (existingRequest)
        {
            summary = "An existing request was returned; no new merge was submitted. " + summary;
        }

        if (status == "failed" && GetString(details, "message") is { } message)
        {
            summary += " " + message;
        }

        return new(status, uuid, summary);
    }

    private static void EnsureResultResponse(
        GitHubAccount account, HttpResponseMessage response, bool submission, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode
            || (submission && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict))
        {
            return;
        }

        if (SsoRequired(account, response) is { } sso) throw CorrelateFailure(response, sso);
        throw CorrelateFailure(response, new GitHubApiException(response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
                "The async merge API or request is unavailable on this host, inaccessible, or expired. Open the PR on GitHub. No legacy merge will be attempted.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "GitHub denied merge access. Check token permissions (Contents: write), repository rules, and single sign-on.",
            HttpStatusCode.UnprocessableEntity => "GitHub rejected the merge request. Check the head SHA, method, and repository rules on GitHub.",
            _ => $"GitHub returned {(int)response.StatusCode}. The merge outcome may be unknown. Check the PR on GitHub before trying again.",
        }, outcomeUnknown: submission && (int)response.StatusCode >= 500));
    }

    private static void EnsureSupport(GitHubAccount account)
    {
        if (!account.Host.IsGitHubDotCom)
        {
            throw new GitHubApiException("Async merge support is not verified for this Enterprise Server host. Merge on GitHub instead. No legacy merge will be attempted.");
        }
    }

    private static Uri RepositoryUri(GitHubAccount account, string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(p => p is "." or ".."))
        {
            throw new GitHubApiException("The repository name must be in owner/name format.");
        }

        return new Uri(account.Host.ApiUrl, $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}");
    }

    private static Uri PullUri(GitHubAccount account, string repository, int number) =>
        number > 0
            ? new Uri(RepositoryUri(account, repository).AbsoluteUri + $"/pulls/{number}")
            : throw new GitHubApiException("A valid pull request number is required.");
}
