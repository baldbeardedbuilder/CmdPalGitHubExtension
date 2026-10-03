// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal enum PullRequestChecksState { Unknown, Pending, Success, Failure }

internal sealed record PullRequestFile(string FileName, string Status, int Additions, int Deletions, Uri? PatchUrl);
internal sealed record PullRequestReview(int Id, string? Author, string State, string? Body, string? CommitId, DateTimeOffset SubmittedAt);
internal sealed record PullRequestCheck(string Name, string Status, string Conclusion, string? DetailsUrl);
internal sealed record PullRequestDetailsSnapshot(
    GitHubPullRequest PullRequest,
    string HeadSha,
    string? BaseSha,
    bool? Mergeable,
    IReadOnlyList<PullRequestFile> Files,
    IReadOnlyList<PullRequestReview> Reviews,
    IReadOnlyList<PullRequestCheck> Checks,
    string? CommitStatus,
    PullRequestChecksState ChecksState,
    string? FilesError,
    string? ReviewsError,
    string? ChecksError,
    string? StatusError,
    bool CanWrite);
internal sealed record PullRequestBranchUpdate(bool Pending, string HeadSha);
internal sealed record PendingPullRequestReview(int Id, string HeadSha, string Body);

internal interface IPullRequestFeatureClient
{
    Task<PullRequestDetailsSnapshot> GetDetailsAsync(GitHubAccount account, string repository, int number, CancellationToken token);
    Task SetDraftAsync(GitHubAccount account, string repository, int number, string expectedHeadSha, bool draft, CancellationToken token);
    Task<PullRequestBranchUpdate> UpdateBranchAsync(GitHubAccount account, string repository, int number, string expectedHeadSha, CancellationToken token);
    Task<PendingPullRequestReview> CreatePendingReviewAsync(GitHubAccount account, string repository, int number,
        string headSha, string body, CancellationToken token);
    Task<PullRequestReview> SubmitReviewAsync(GitHubAccount account, string repository, int number,
        PendingPullRequestReview review, string eventName, CancellationToken token);
}

internal sealed partial class PullRequestActionsClient
{
    private readonly GitHubGraphQLClient _graphQL = new GitHubGraphQLClient(httpClient);

    public Task<PullRequestDetailsSnapshot> GetDetailsAsync(
        GitHubAccount account, string repository, int number, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.PullRequests, async () =>
        {
            var pull = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
            var filesTask = ReadPartAsync(() => ReadFilesAsync(account, repository, number, token), token);
            var reviewsTask = ReadPartAsync(() => ReadReviewsAsync(account, repository, number, token), token);
            var checksTask = ReadPartAsync(() => ReadChecksAsync(account, repository, pull.HeadSha!, token), token);
            var statusTask = ReadPartAsync(() => ReadStatusAsync(account, repository, pull.HeadSha!, token), token);
            await Task.WhenAll(filesTask, reviewsTask, checksTask, statusTask).ConfigureAwait(false);
            var files = await filesTask.ConfigureAwait(false);
            var reviews = await reviewsTask.ConfigureAwait(false);
            var checks = await checksTask.ConfigureAwait(false);
            var status = await statusTask.ConfigureAwait(false);
            return new PullRequestDetailsSnapshot(
                pull.PullRequest, pull.HeadSha!, pull.BaseSha, pull.Mergeable,
                files.Value ?? [], reviews.Value ?? [], checks.Value ?? [], status.Value,
                GetChecksState(checks.Value, status.Value, checks.Error is null, status.Error is null),
                files.Error, reviews.Error, checks.Error, status.Error, pull.CanWrite);
        }, cancellationToken: token);

    public async Task SetDraftAsync(
        GitHubAccount account, string repository, int number, string expectedHeadSha, bool draft, CancellationToken token)
    {
        var current = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
        EnsureRepositoryWrite(current.CanWrite);
        if (current.PullRequest.State is not (SubjectState.Open or SubjectState.Draft) || current.Draft == draft)
            throw new GitHubApiException("The pull request state changed. Refresh and review the draft action again.");
        EnsureHead(current.HeadSha, expectedHeadSha);
        if (string.IsNullOrWhiteSpace(current.PullRequest.NodeId))
            throw new GitHubApiException("GitHub didn't provide a pull request ID. Refresh before changing draft status.");

        var action = draft ? "convertPullRequestToDraft" : "markPullRequestReadyForReview";
        var query = draft
            ? "mutation ConvertPullRequestToDraft($id: ID!) { convertPullRequestToDraft(input: {pullRequestId: $id}) { pullRequest { isDraft } } }"
            : "mutation MarkPullRequestReadyForReview($id: ID!) { markPullRequestReadyForReview(input: {pullRequestId: $id}) { pullRequest { isDraft } } }";
        var result = await _graphQL.ExecuteAsync(account, query, new JsonObject { ["id"] = current.PullRequest.NodeId },
            token, draft ? "ConvertPullRequestToDraft" : "MarkPullRequestReadyForReview").ConfigureAwait(false);
        var verified = result.Data is { } data && data.TryGetProperty(action, out var mutation)
            && mutation.ValueKind == JsonValueKind.Object && mutation.TryGetProperty("pullRequest", out var updated)
            && updated.ValueKind == JsonValueKind.Object && updated.TryGetProperty("isDraft", out var isDraft)
            && isDraft.ValueKind is JsonValueKind.True or JsonValueKind.False && isDraft.GetBoolean() == draft;
        if (!result.IsSuccess || !verified)
            throw new GitHubApiException(result.Errors.Count > 0
                ? "GitHub rejected the draft status change. This GitHub host may not support the required GraphQL field."
                : "GitHub didn't confirm the draft status change. Refresh before trying again.", outcomeUnknown: true);
        var after = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
        if (after.HeadSha != expectedHeadSha || after.Draft != draft)
            throw new GitHubApiException("GitHub accepted the draft request, but the pull request changed before it could be verified.",
                outcomeUnknown: true);
    }

    public async Task<PullRequestBranchUpdate> UpdateBranchAsync(
        GitHubAccount account, string repository, int number, string expectedHeadSha, CancellationToken token)
    {
        var current = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
        EnsureRepositoryWrite(current.CanWrite);
        if (current.PullRequest.State is not (SubjectState.Open or SubjectState.Draft))
            throw new GitHubApiException("Only open pull requests can be updated from the base branch.");
        EnsureHead(current.HeadSha, expectedHeadSha);

        using var content = JsonContent(new UpdateBranchRequest(expectedHeadSha));
        using var response = await SendAsync(httpClient, account, HttpMethod.Put, PullUri(account, repository, number, "update-branch"),
            token, throwOnError: false, content: content).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict || response.StatusCode == HttpStatusCode.UnprocessableEntity)
            throw new GitHubApiException("GitHub rejected the update because the expected head SHA is stale or the branch can't be updated.");
        if (!response.IsSuccessStatusCode)
        {
            using var ignored = await ReadJsonAsync(response, token).ConfigureAwait(false);
            throw new GitHubApiException("GitHub couldn't update this branch. Refresh before trying again.",
                outcomeUnknown: (int)response.StatusCode >= 500);
        }

        var pending = response.StatusCode == HttpStatusCode.Accepted;
        var refreshed = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
        return new PullRequestBranchUpdate(pending, refreshed.HeadSha!);
    }

    public async Task<PendingPullRequestReview> CreatePendingReviewAsync(
        GitHubAccount account, string repository, int number, string headSha, string body, CancellationToken token)
    {
        var current = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
        EnsureReviewable(current, account, headSha);
        using var content = JsonContent(new CreatePendingReviewRequest(headSha, body.Trim()));
        using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post,
            PullUri(account, repository, number, "reviews"), token, content: content).ConfigureAwait(false);
        if (response.IsAccepted || response.Json is null)
            throw new GitHubApiException("GitHub may have created a pending review, but didn't confirm it. Refresh reviews before retrying.",
                outcomeUnknown: true);
        var review = ParseReview(response.Json.RootElement, mutation: true);
        if (review.State != "PENDING" || review.CommitId != headSha
            || !string.Equals(review.Body ?? string.Empty, body.Trim(), StringComparison.Ordinal))
            throw new GitHubApiException("GitHub didn't confirm the pending review draft. Refresh before trying again.", outcomeUnknown: true);
        return new PendingPullRequestReview(review.Id, headSha, body.Trim());
    }

    public async Task<PullRequestReview> SubmitReviewAsync(
        GitHubAccount account, string repository, int number, PendingPullRequestReview review, string eventName, CancellationToken token)
    {
        if (eventName is not ("APPROVE" or "REQUEST_CHANGES" or "COMMENT"))
            throw new GitHubApiException("Choose approve, request changes, or comment.");
        if (eventName == "REQUEST_CHANGES") RequireReviewBody(review.Body);
        var current = await ReadPullAsync(account, repository, number, token).ConfigureAwait(false);
        EnsureReviewable(current, account, review.HeadSha);
        var existing = await ReadReviewAsync(account, repository, number, review.Id, token).ConfigureAwait(false);
        if (existing.State != "PENDING" || existing.CommitId != review.HeadSha
            || !string.Equals(existing.Author, account.Login, StringComparison.OrdinalIgnoreCase))
        {
            if (existing.State == SubmittedReviewState(eventName))
                return existing;
            throw new GitHubApiException("The pending review changed or is no longer yours. Refresh before submitting it.");
        }

        using var content = JsonContent(new SubmitReviewRequest(eventName, review.Body));
        using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post,
            PullUri(account, repository, number, $"reviews/{review.Id.ToString(CultureInfo.InvariantCulture)}/events"),
            token, content: content).ConfigureAwait(false);
        if (response.IsAccepted || response.Json is null)
            throw new GitHubApiException("The review may have been submitted. Refresh the pull request reviews before trying again.",
                outcomeUnknown: true);
        var submitted = ParseReview(response.Json.RootElement, mutation: true);
        if (submitted.Id != review.Id || submitted.State != SubmittedReviewState(eventName) || submitted.CommitId != review.HeadSha)
            throw new GitHubApiException("GitHub didn't confirm the submitted review. Refresh before trying again.", outcomeUnknown: true);
        return submitted;
    }

    private async Task<(GitHubPullRequest PullRequest, string? HeadSha, string? BaseSha, bool? Mergeable, bool Draft, bool CanWrite)>
        ReadPullAsync(GitHubAccount account, string repository, int number, CancellationToken token)
    {
        using var repoResponse = await SendAsync(httpClient, account, HttpMethod.Get, RepoUri(account, repository, null), token)
            .ConfigureAwait(false);
        using var repoJson = await ReadJsonAsync(repoResponse, token).ConfigureAwait(false);
        var permissions = repoJson.RootElement.ValueKind == JsonValueKind.Object
            && repoJson.RootElement.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Object
                ? perms : default;
        var canWrite = GetBool(permissions, "push") || GetBool(permissions, "maintain") || GetBool(permissions, "admin");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, PullUri(account, repository, number), token)
            .ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || GetInt(root, "number") != number
            || !root.TryGetProperty("head", out var head) || head.ValueKind != JsonValueKind.Object
            || GetString(head, "sha") is not { Length: > 0 } headSha)
            throw new GitHubApiException("GitHub sent back pull request details we couldn't verify.");
        var baseSha = root.TryGetProperty("base", out var baseBranch) && baseBranch.ValueKind == JsonValueKind.Object
            ? GetString(baseBranch, "sha") : null;
        bool? mergeable = root.TryGetProperty("mergeable", out var merge) && merge.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? merge.GetBoolean() : null;
        var pull = GitHubPullRequest.Parse(root, SubjectStateParser.Parse(root));
        return (pull, headSha, baseSha, mergeable, GetBool(root, "draft"), canWrite);
    }

    private async Task<IReadOnlyList<PullRequestFile>> ReadFilesAsync(
        GitHubAccount account, string repository, int number, CancellationToken token)
    {
        var endpoint = PullUri(account, repository, number, "files");
        var values = await ReadPagedAsync(account, endpoint, token).ConfigureAwait(false);
        return values.Select(item =>
        {
            if (GetString(item, "filename") is not { Length: > 0 } name)
                throw new GitHubApiException("GitHub sent back a changed file we couldn't read.");
            return new PullRequestFile(name, GetString(item, "status") ?? "unknown", GetInt(item, "additions"),
                GetInt(item, "deletions"), GetUri(item, "blob_url"));
        }).ToArray();
    }

    private async Task<IReadOnlyList<PullRequestReview>> ReadReviewsAsync(
        GitHubAccount account, string repository, int number, CancellationToken token)
    {
        var endpoint = PullUri(account, repository, number, "reviews");
        return (await ReadPagedAsync(account, endpoint, token).ConfigureAwait(false))
            .Select(item => ParseReview(item)).ToArray();
    }

    private async Task<IReadOnlyList<PullRequestCheck>> ReadChecksAsync(
        GitHubAccount account, string repository, string headSha, CancellationToken token)
    {
        var uri = new Uri(RepoUri(account, repository, $"commits/{Uri.EscapeDataString(headSha)}/check-runs").AbsoluteUri + "?per_page=100");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        var runs = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("check_runs", out var value)
            ? value : default;
        if (runs.ValueKind != JsonValueKind.Array)
            throw new GitHubApiException("GitHub sent back check runs we couldn't read.");
        return runs.EnumerateArray().Select(run => new PullRequestCheck(
            GetString(run, "name") ?? "Unnamed check",
            GetString(run, "status") ?? "unknown",
            GetString(run, "conclusion") ?? string.Empty,
            GetString(run, "details_url"))).ToArray();
    }

    private async Task<string?> ReadStatusAsync(
        GitHubAccount account, string repository, string headSha, CancellationToken token)
    {
        var uri = RepoUri(account, repository, $"commits/{Uri.EscapeDataString(headSha)}/status");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        var state = GetString(json.RootElement, "state");
        if (state is not ("success" or "failure" or "error" or "pending"))
            throw new GitHubApiException("GitHub sent back commit status we couldn't read.");
        return state;
    }

    private async Task<JsonElement[]> ReadPagedAsync(GitHubAccount account, Uri endpoint, CancellationToken token)
    {
        var values = new List<JsonElement>();
        Uri? page = new(endpoint.AbsoluteUri + "?per_page=100");
        var visited = new HashSet<Uri>();
        while (page is not null)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(page)) throw new GitHubApiException("GitHub repeated a pull request detail page.");
            RequirePage(endpoint, page);
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, page, token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
                throw new GitHubApiException("GitHub sent back a pull request detail list we couldn't read.");
            values.AddRange(json.RootElement.EnumerateArray().Select(item => item.Clone()));
            page = NextPage(response);
            if (page is not null) RequirePage(endpoint, page);
        }
        return [.. values];
    }

    private async Task<PullRequestReview> ReadReviewAsync(
        GitHubAccount account, string repository, int number, int reviewId, CancellationToken token)
    {
        using var response = await SendAsync(httpClient, account, HttpMethod.Get,
            PullUri(account, repository, number, $"reviews/{reviewId.ToString(CultureInfo.InvariantCulture)}"), token).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
        return ParseReview(json.RootElement);
    }

    private static PullRequestReview ParseReview(JsonElement root, bool mutation = false)
    {
        if (root.ValueKind != JsonValueKind.Object || GetInt(root, "id") <= 0
            || GetString(root, "state") is not { Length: > 0 } state)
            throw new GitHubApiException("GitHub sent back a pull request review we couldn't verify.", outcomeUnknown: mutation);
        var author = root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? GetString(user, "login") : null;
        return new PullRequestReview(GetInt(root, "id"), author, state, GetString(root, "body"),
            GetString(root, "commit_id"), GetDate(root, "submitted_at"));
    }

    private static async Task<PartResult<T>> ReadPartAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        try { return new PartResult<T>(await action().ConfigureAwait(false), null); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new PartResult<T>(default, "GitHub took too long to load this section. Try refreshing the pull request."); }
        catch (GitHubApiException) { return new PartResult<T>(default, "GitHub couldn't load this section. Try refreshing the pull request."); }
        catch (HttpRequestException) { return new PartResult<T>(default, "The connection failed while loading this section. Try refreshing the pull request."); }
    }

    private static PullRequestChecksState GetChecksState(
        IReadOnlyList<PullRequestCheck>? checks, string? status, bool checksLoaded, bool statusLoaded)
    {
        if (!checksLoaded && !statusLoaded) return PullRequestChecksState.Unknown;
        var checkValues = checks ?? [];
        if (checkValues.Any(check => check.Conclusion is "failure" or "cancelled" or "timed_out" or "action_required")
            || status is "failure" or "error")
            return PullRequestChecksState.Failure;
        if (checkValues.Any(check => check.Status == "completed"
            && check.Conclusion is not ("success" or "neutral" or "skipped" or "")))
            return PullRequestChecksState.Failure;
        if (checkValues.Any(check => check.Status != "completed") || status == "pending")
            return PullRequestChecksState.Pending;
        if (checkValues.Any(check => check.Status == "completed" && check.Conclusion.Length == 0))
            return PullRequestChecksState.Unknown;
        return checksLoaded && statusLoaded && (checkValues.Count > 0 || status is not null)
            ? PullRequestChecksState.Success : PullRequestChecksState.Unknown;
    }

    private static void EnsureReviewable(
        (GitHubPullRequest PullRequest, string? HeadSha, string? BaseSha, bool? Mergeable, bool Draft, bool CanWrite) current,
        GitHubAccount account, string headSha)
    {
        if (current.PullRequest.State != SubjectState.Open)
            throw new GitHubApiException("Only open pull requests can receive a review.");
        if (string.Equals(current.PullRequest.Author, account.Login, StringComparison.OrdinalIgnoreCase))
            throw new GitHubApiException("GitHub doesn't allow you to review your own pull request.");
        EnsureHead(current.HeadSha, headSha);
    }

    private static string SubmittedReviewState(string eventName) => eventName switch
    {
        "APPROVE" => "APPROVED",
        "REQUEST_CHANGES" => "CHANGES_REQUESTED",
        "COMMENT" => "COMMENTED",
        _ => string.Empty,
    };

    private static void EnsureHead(string? current, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(current, expected, StringComparison.Ordinal))
            throw new GitHubApiException("The pull request head commit changed. Refresh and review the current commit.");
    }

    private static void EnsureRepositoryWrite(bool canWrite)
    {
        if (!canWrite) throw new GitHubApiException("Write access to this repository is required for this pull request action.");
    }

    private static void RequireReviewBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new GitHubApiException("Enter review text before submitting.");
    }

    private static void RequirePage(Uri endpoint, Uri page)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps || page.Authority != endpoint.Authority
            || page.AbsolutePath != endpoint.AbsolutePath || page.UserInfo.Length != 0 || page.Fragment.Length != 0)
            throw new GitHubApiException("GitHub sent back an unexpected pull request detail page.");
    }

    private static Uri PullUri(GitHubAccount account, string repository, int number, string? suffix = null)
    {
        if (number <= 0) throw new GitHubApiException("A valid pull request number is required.");
        var route = RepoUri(account, repository, null).AbsoluteUri + $"/pulls/{number.ToString(CultureInfo.InvariantCulture)}";
        return new Uri(route + (suffix is null ? string.Empty : "/" + suffix));
    }

    private static StringContent JsonContent(UpdateBranchRequest value) =>
        new(JsonSerializer.Serialize(value, PullRequestFeatureJsonContext.Default.UpdateBranchRequest), Encoding.UTF8, "application/json");

    private static StringContent JsonContent(CreatePendingReviewRequest value) =>
        new(JsonSerializer.Serialize(value, PullRequestFeatureJsonContext.Default.CreatePendingReviewRequest), Encoding.UTF8, "application/json");

    private static StringContent JsonContent(SubmitReviewRequest value) =>
        new(JsonSerializer.Serialize(value, PullRequestFeatureJsonContext.Default.SubmitReviewRequest), Encoding.UTF8, "application/json");

    private sealed record PartResult<T>(T? Value, string? Error);
}
