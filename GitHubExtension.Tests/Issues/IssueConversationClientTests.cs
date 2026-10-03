// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using static BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues.IssueMutationTestData;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueConversationClientTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task CreateComment_UsesIssueCommentsRouteAndValidatesReturnedTarget()
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(Comment(12, 42, "New comment"))));
        using var http = new HttpClient(handler);

        var result = await new IssueConversationClient(http).CreateCommentAsync(
            Account, "octo/tool", 42, "New comment", TestContext.CancellationToken);

        var request = Assert.ContainsSingle(handler.Requests);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(new Uri("https://api.github.com/repos/octo/tool/issues/42/comments"), request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.AreEqual("New comment", body.RootElement.GetProperty("body").GetString());
        Assert.AreEqual(12, result.Id);
    }

    [TestMethod]
    public async Task EditComment_RequiresOwnershipAndVerifiesEditedComment()
    {
        using var handler = new IssueTestHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Response(Comment(12, 42, "Old comment", Account.Login)));
            return Task.FromResult(Response(Comment(12, 42, "Edited comment", Account.Login)));
        });
        using var http = new HttpClient(handler);

        var result = await new IssueConversationClient(http).EditCommentAsync(
            Account, "octo/tool", 42, 12, "Edited comment", TestContext.CancellationToken);

        Assert.AreEqual(HttpMethod.Get, handler.Requests[0].Method);
        Assert.AreEqual(HttpMethod.Patch, handler.Requests[1].Method);
        Assert.AreEqual(new Uri("https://api.github.com/repos/octo/tool/issues/comments/12"), handler.Requests[1].Uri);
        Assert.AreEqual("Edited comment", result.Body);
    }

    [TestMethod]
    public async Task EditComment_RejectsDifferentAuthorBeforeMutation()
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(Comment(12, 42, "Other's comment", "mona"))));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssueConversationClient(http).EditCommentAsync(Account, "octo/tool", 42, 12,
                "Attempted edit", TestContext.CancellationToken));

        Assert.Contains("Only the comment author", error.Message);
        Assert.AreEqual(HttpMethod.Get, Assert.ContainsSingle(handler.Requests).Method);
    }

    [TestMethod]
    public async Task DeleteComment_RequiresMaintainerPermissionAndNoContentResponse()
    {
        using var handler = new IssueTestHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/comments/12", StringComparison.Ordinal))
                return Task.FromResult(Response(Comment(12, 42, "Comment", "mona")));
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(Response("""{"permissions":{"maintain":true}}"""));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler);

        await new IssueConversationClient(http).DeleteCommentAsync(Account, "octo/tool", 42, 12, TestContext.CancellationToken);

        Assert.AreEqual(HttpMethod.Delete, handler.Requests.Last().Method);
        Assert.AreEqual(new Uri("https://api.github.com/repos/octo/tool/issues/comments/12"), handler.Requests.Last().Uri);
    }

    [TestMethod]
    public async Task GetComments_RejectsWrongTargetWithoutReturningComments()
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("[" + Comment(12, 43, "Wrong issue") + "]")));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssueConversationClient(http).GetCommentsAsync(Account, "octo/tool", 42, null, TestContext.CancellationToken));

        Assert.Contains("different issue or pull request", error.Message);
    }

    [TestMethod]
    public async Task GetComments_FollowsValidatedPagination()
    {
        var next = new Uri("https://api.github.com/repos/octo/tool/issues/42/comments?per_page=100&page=2");
        using var handler = new IssueTestHandler((request, _, _) =>
            Task.FromResult(Response("[" + Comment(12, 42, request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                ? "Second page" : "First page") + "]", next: request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? null : next)));
        using var http = new HttpClient(handler);
        var client = new IssueConversationClient(http);

        var first = await client.GetCommentsAsync(Account, "octo/tool", 42, null, TestContext.CancellationToken);
        var second = await client.GetCommentsAsync(Account, "octo/tool", 42, first.NextPage, TestContext.CancellationToken);

        Assert.AreEqual(next, first.NextPage);
        Assert.AreEqual("First page", first.Comments.Single().Body);
        Assert.IsNull(second.NextPage);
        Assert.AreEqual("Second page", second.Comments.Single().Body);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task DeleteComment_DeniedMaintainerCannotDelete()
    {
        using var handler = new IssueTestHandler((request, _, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/comments/12", StringComparison.Ordinal))
                return Task.FromResult(Response(Comment(12, 42, "Comment", "mona")));
            return Task.FromResult(Response("""{"permissions":{"push":false,"maintain":false,"admin":false}}"""));
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssueConversationClient(http).DeleteCommentAsync(Account, "octo/tool", 42, 12, TestContext.CancellationToken));

        Assert.Contains("repository maintainer", error.Message);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    private static string Comment(int id, int issue, string body, string? author = "octocat") =>
        $$"""{"id":{{id}},"body":{{GitHubJson.String(body)}},"user":{"login":{{GitHubJson.String(author ?? string.Empty)}}},"created_at":"2025-06-01T10:00:00Z","updated_at":"2025-06-01T11:00:00Z","issue_url":"https://api.github.com/repos/octo/tool/issues/{{issue}}"}""";
}
