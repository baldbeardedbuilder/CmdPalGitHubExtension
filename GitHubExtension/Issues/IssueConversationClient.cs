// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal sealed record IssueComment(int Id, string Body, string? Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Uri? IssueApiUrl);

internal sealed record IssueCommentsPage(IReadOnlyList<IssueComment> Comments, Uri? NextPage);

internal interface IIssueConversationClient
{
    Task<IssueCommentsPage> GetCommentsAsync(GitHubAccount account, string repository, int number, Uri? page, CancellationToken token);
    Task<IssueComment> CreateCommentAsync(GitHubAccount account, string repository, int number, string body, CancellationToken token);
    Task<IssueComment> EditCommentAsync(GitHubAccount account, string repository, int number, int commentId, string body, CancellationToken token);
    Task DeleteCommentAsync(GitHubAccount account, string repository, int number, int commentId, CancellationToken token);
}

internal sealed class IssueConversationClient(HttpClient httpClient, DiagnosticArea area = DiagnosticArea.Issues) : IIssueConversationClient
{
    private const int PageSize = 100;

    public Task<IssueCommentsPage> GetCommentsAsync(
        GitHubAccount account, string repository, int number, Uri? page, CancellationToken token) =>
        DomainDiagnostics.RunAsync(area, async () =>
        {
            var endpoint = IssueUri(account, repository, number, "comments");
            var uri = page ?? new Uri(endpoint.AbsoluteUri + $"?per_page={PageSize}");
            RequirePage(endpoint, uri);
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var comments = ParseComments(json.RootElement);
            var expectedIssueUrl = IssueResourceUri(account, repository, number);
            if (comments.Any(comment => comment.IssueApiUrl != expectedIssueUrl))
                throw new GitHubApiException("GitHub returned comments for a different issue or pull request.");
            var next = NextPage(response);
            if (next is not null) RequirePage(endpoint, next);
            return new IssueCommentsPage(comments, next);
        }, cancellationToken: token);

    public Task<IssueComment> CreateCommentAsync(
        GitHubAccount account, string repository, int number, string body, CancellationToken token) =>
        DomainDiagnostics.RunAsync(area, async () =>
        {
            RequireBody(body);
            using var content = JsonContent(new IssueCommentRequest(body));
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post,
                IssueUri(account, repository, number, "comments"), token, content: content).ConfigureAwait(false);
            if (response.IsAccepted || response.Json is null)
                throw new GitHubApiException("GitHub hasn't confirmed the comment. Refresh before trying again.", outcomeUnknown: true);
            return RequireTarget(ParseComment(response.Json.RootElement, mutation: true), account, repository, number, mutation: true);
        }, cancellationToken: token);

    public Task<IssueComment> EditCommentAsync(
        GitHubAccount account, string repository, int number, int commentId, string body, CancellationToken token) =>
        DomainDiagnostics.RunAsync(area, async () =>
        {
            RequireBody(body);
            await RequireCommentPermissionAsync(account, repository, number, commentId, edit: true, token).ConfigureAwait(false);
            using var content = JsonContent(new IssueCommentRequest(body));
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Patch,
                CommentUri(account, repository, commentId), token, content: content).ConfigureAwait(false);
            if (response.IsAccepted || response.Json is null)
                throw new GitHubApiException("GitHub hasn't confirmed the comment edit. Refresh before trying again.", outcomeUnknown: true);
            var comment = RequireTarget(ParseComment(response.Json.RootElement, mutation: true), account, repository, number, mutation: true);
            if (comment.Id != commentId)
                throw new GitHubApiException("GitHub returned a different comment than the one edited.", outcomeUnknown: true);
            return comment;
        }, cancellationToken: token);

    public async Task DeleteCommentAsync(
        GitHubAccount account, string repository, int number, int commentId, CancellationToken token)
    {
        await DomainDiagnostics.RunAsync(area, async () =>
        {
            await RequireCommentPermissionAsync(account, repository, number, commentId, edit: false, token).ConfigureAwait(false);
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Delete,
                CommentUri(account, repository, commentId), token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new GitHubApiException("GitHub didn't confirm comment deletion. Refresh before trying again.", outcomeUnknown: true);
            return true;
        }, cancellationToken: token).ConfigureAwait(false);
    }

    private async Task RequireCommentPermissionAsync(
        GitHubAccount account, string repository, int number, int commentId, bool edit, CancellationToken token)
    {
        if (commentId <= 0) throw new GitHubApiException("Choose a valid comment.");
        using var commentResponse = await SendAsync(httpClient, account, HttpMethod.Get,
            CommentUri(account, repository, commentId), token).ConfigureAwait(false);
        using var commentJson = await ReadJsonAsync(commentResponse, token).ConfigureAwait(false);
        var comment = RequireTarget(ParseComment(commentJson.RootElement), account, repository, number);
        var isAuthor = string.Equals(comment.Author, account.Login, StringComparison.OrdinalIgnoreCase);
        if (isAuthor) return;
        if (edit)
            throw new GitHubApiException("Only the comment author can edit this comment.");

        using var repoResponse = await SendAsync(httpClient, account, HttpMethod.Get, RepositoryUri(account, repository), token)
            .ConfigureAwait(false);
        using var repoJson = await ReadJsonAsync(repoResponse, token).ConfigureAwait(false);
        var permissions = repoJson.RootElement.ValueKind == JsonValueKind.Object
            && repoJson.RootElement.TryGetProperty("permissions", out var value) && value.ValueKind == JsonValueKind.Object
                ? value : default;
        if (!GetBool(permissions, "push") && !GetBool(permissions, "maintain") && !GetBool(permissions, "admin"))
            throw new GitHubApiException("Only the comment author or a repository maintainer can delete this comment.");
    }

    private List<IssueComment> ParseComments(JsonElement value) =>
        DomainDiagnostics.Read(area, () =>
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw new GitHubApiException("GitHub sent back a comment list we couldn't read.");
            return value.EnumerateArray().Select(item => ParseComment(item)).ToList();
        });

    private IssueComment ParseComment(JsonElement value, bool mutation = false) =>
        DomainDiagnostics.Read(area, () =>
        {
            if (value.ValueKind != JsonValueKind.Object || GetInt(value, "id") <= 0
                || GetString(value, "body") is not { } body)
                throw new GitHubApiException("GitHub sent back a comment we couldn't verify.", outcomeUnknown: mutation);
            var user = value.TryGetProperty("user", out var author) && author.ValueKind == JsonValueKind.Object
                ? GetString(author, "login") : null;
            return new IssueComment(GetInt(value, "id"), body, user, GetDate(value, "created_at"), GetDate(value, "updated_at"),
                GetUri(value, "issue_url"));
        });

    private static IssueComment RequireTarget(
        IssueComment comment, GitHubAccount account, string repository, int number, bool mutation = false)
    {
        if (comment.IssueApiUrl != IssueResourceUri(account, repository, number))
            throw new GitHubApiException("GitHub returned a comment for a different issue or pull request.",
                outcomeUnknown: mutation);
        return comment;
    }

    private static void RequireBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new GitHubApiException("Enter a comment before submitting.");
    }

    private static void RequirePage(Uri endpoint, Uri page)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps || page.Authority != endpoint.Authority
            || page.AbsolutePath != endpoint.AbsolutePath || page.UserInfo.Length != 0 || page.Fragment.Length != 0)
            throw new GitHubApiException("GitHub sent back an unexpected comment page.");
    }

    private static Uri IssueUri(GitHubAccount account, string repository, int number, string suffix)
    {
        return new Uri(IssueResourceUri(account, repository, number).AbsoluteUri + "/" + suffix);
    }

    private static Uri IssueResourceUri(GitHubAccount account, string repository, int number) =>
        number > 0
            ? new Uri(RepositoryUri(account, repository).AbsoluteUri + $"/issues/{number.ToString(CultureInfo.InvariantCulture)}")
            : throw new GitHubApiException("Choose a valid issue or pull request number.");

    private static Uri CommentUri(GitHubAccount account, string repository, int commentId)
    {
        if (commentId <= 0) throw new GitHubApiException("Choose a valid comment.");
        return new Uri(RepositoryUri(account, repository).AbsoluteUri
            + $"/issues/comments/{commentId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static Uri RepositoryUri(GitHubAccount account, string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.Any(char.IsWhiteSpace) || part.Contains('\\')))
            throw new GitHubApiException("The repository name must be in owner/name format.");
        return new Uri(account.Host.ApiUrl,
            $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}");
    }

    private static StringContent JsonContent(IssueCommentRequest request) =>
        new(JsonSerializer.Serialize(request, IssueConversationJsonContext.Default.IssueCommentRequest),
            Encoding.UTF8, "application/json");
}
