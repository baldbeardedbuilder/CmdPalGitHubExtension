// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class PullRequestFeaturesClientTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task UpdateBranch_PinsExpectedHeadAndReportsAcceptedPendingOperation()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/update-branch", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.Accepted, string.Empty));
            return Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull(HeadSha)
                : """{"permissions":{"push":true}}"""));
        });

        var result = await test.Client.UpdateBranchAsync(Account, "octo/tool", 7, HeadSha, TestContext.CancellationToken);

        var update = test.Handler.Requests.Single(request => request.Method == HttpMethod.Put);
        Assert.AreEqual("https://api.github.com/repos/octo/tool/pulls/7/update-branch", update.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(update.Body!);
        Assert.AreEqual(HeadSha, body.RootElement.GetProperty("expected_head_sha").GetString());
        Assert.IsTrue(result.Pending);
        Assert.AreEqual(HeadSha, result.HeadSha);
    }

    [TestMethod]
    public async Task UpdateBranch_RejectsStaleExpectedHeadBeforeSendingMutation()
    {
        using var test = new FeatureClientBundle((request, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull("new-head")
                : """{"permissions":{"push":true}}""")));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            test.Client.UpdateBranchAsync(Account, "octo/tool", 7, HeadSha, TestContext.CancellationToken));

        Assert.Contains("head commit changed", error.Message);
        Assert.IsTrue(test.Handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task UpdateBranch_ReportsConflictWithoutClaimingSuccess()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/update-branch", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.Conflict, "{}"));
            return Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull(HeadSha)
                : """{"permissions":{"push":true}}"""));
        });

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            test.Client.UpdateBranchAsync(Account, "octo/tool", 7, HeadSha, TestContext.CancellationToken));

        Assert.Contains("stale", error.Message);
        Assert.IsFalse(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task SetDraft_UsesGraphQLMutationAndVerifiesExpectedHeadAndResult()
    {
        var draft = false;
        using var test = new FeatureClientBundle((request, _) =>
        {
            if (request.RequestUri == Account.Host.GraphQLUrl)
            {
                draft = true;
                return Task.FromResult(Response(HttpStatusCode.OK,
                    """{"data":{"convertPullRequestToDraft":{"pullRequest":{"isDraft":true}}}}"""));
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, Pull(HeadSha, draft)));
            return Task.FromResult(Response(HttpStatusCode.OK, """{"permissions":{"push":true}}"""));
        });

        await test.Client.SetDraftAsync(Account, "octo/tool", 7, HeadSha, draft: true, TestContext.CancellationToken);

        var mutation = test.Handler.Requests.Single(request => request.Uri == Account.Host.GraphQLUrl);
        Assert.Contains("convertPullRequestToDraft", mutation.Body!);
        Assert.Contains("PR_7", mutation.Body!);
        Assert.IsTrue(draft);
    }

    [TestMethod]
    public async Task SetDraft_MarksDraftPullRequestReady()
    {
        var draft = true;
        using var test = new FeatureClientBundle((request, _) =>
        {
            if (request.RequestUri == Account.Host.GraphQLUrl)
            {
                draft = false;
                return Task.FromResult(Response(HttpStatusCode.OK,
                    """{"data":{"markPullRequestReadyForReview":{"pullRequest":{"isDraft":false}}}}"""));
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, Pull(HeadSha, draft)));
            return Task.FromResult(Response(HttpStatusCode.OK, """{"permissions":{"push":true}}"""));
        });

        await test.Client.SetDraftAsync(Account, "octo/tool", 7, HeadSha, draft: false, TestContext.CancellationToken);

        var mutation = test.Handler.Requests.Single(request => request.Uri == Account.Host.GraphQLUrl);
        Assert.Contains("markPullRequestReadyForReview", mutation.Body!);
    }

    [TestMethod]
    public async Task CreatePendingReview_PinsReviewBodyAndCommit()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(Response(HttpStatusCode.OK, Review(21, "PENDING", HeadSha, "Please fix this.")));
            return Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull(HeadSha)
                : """{"permissions":{"push":true}}"""));
        });

        var pending = await test.Client.CreatePendingReviewAsync(
            Account, "octo/tool", 7, HeadSha, "Please fix this.", TestContext.CancellationToken);

        var request = test.Handler.Requests.Single(item => item.Method == HttpMethod.Post);
        Assert.AreEqual("https://api.github.com/repos/octo/tool/pulls/7/reviews", request.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual(HeadSha, body.RootElement.GetProperty("commit_id").GetString());
        Assert.AreEqual("Please fix this.", body.RootElement.GetProperty("body").GetString());
        Assert.AreEqual(21, pending.Id);
        Assert.AreEqual(HeadSha, pending.HeadSha);
    }

    [TestMethod]
    public async Task CreatePendingReview_RejectsStaleHeadBeforeMutation()
    {
        using var test = new FeatureClientBundle((request, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull("new-head")
                : """{"permissions":{"push":true}}""")));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => test.Client.CreatePendingReviewAsync(
            Account, "octo/tool", 7, HeadSha, "Review", TestContext.CancellationToken));

        Assert.Contains("head commit changed", error.Message);
        Assert.IsTrue(test.Handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task CreatePendingReview_RefusesSelfReviewBeforeMutation()
    {
        using var test = new FeatureClientBundle((request, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri!.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull(HeadSha, author: Account.Login)
                : """{"permissions":{"push":true}}""")));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => test.Client.CreatePendingReviewAsync(
            Account, "octo/tool", 7, HeadSha, "Review", TestContext.CancellationToken));

        Assert.Contains("review your own pull request", error.Message);
        Assert.IsTrue(test.Handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task SubmitReview_UsesExistingPendingReviewAndRequestedEvent()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(Response(HttpStatusCode.OK, Review(21, "APPROVED", HeadSha, "Looks good.")));
            if (request.RequestUri!.AbsolutePath.EndsWith("/reviews/21", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.OK, Review(21, "PENDING", HeadSha, "Looks good.")));
            return Task.FromResult(Response(HttpStatusCode.OK, request.RequestUri.AbsolutePath.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull(HeadSha)
                : """{"permissions":{"push":true}}"""));
        });

        var submitted = await test.Client.SubmitReviewAsync(Account, "octo/tool", 7,
            new PendingPullRequestReview(21, HeadSha, "Looks good."), "APPROVE", TestContext.CancellationToken);

        var request = test.Handler.Requests.Single(item => item.Method == HttpMethod.Post);
        Assert.AreEqual("https://api.github.com/repos/octo/tool/pulls/7/reviews/21/events", request.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual("APPROVE", body.RootElement.GetProperty("event").GetString());
        Assert.AreEqual("Looks good.", body.RootElement.GetProperty("body").GetString());
        Assert.AreEqual("APPROVED", submitted.State);
    }

    [TestMethod]
    public async Task GetDetails_LoadsNativeSectionsAndClassifiesPendingChecks()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var payload = path.EndsWith("/pulls/7", StringComparison.Ordinal) ? Pull(HeadSha)
                : path.EndsWith("/pulls/7/files", StringComparison.Ordinal)
                    ? """[{"filename":"src/login.cs","status":"modified","additions":5,"deletions":2,"blob_url":"https://github.com/octo/tool/blob/main/src/login.cs"}]"""
                : path.EndsWith("/pulls/7/reviews", StringComparison.Ordinal)
                    ? """[{"id":9,"state":"APPROVED","body":"Looks good","commit_id":"head-sha-123","submitted_at":"2025-06-01T11:00:00Z","user":{"login":"mona"}}]"""
                : path.EndsWith("/check-runs", StringComparison.Ordinal)
                    ? """{"check_runs":[{"name":"CI","status":"in_progress","conclusion":null}]}"""
                : path.EndsWith("/status", StringComparison.Ordinal)
                    ? """{"state":"pending"}"""
                : """{"permissions":{"push":true}}""";
            return Task.FromResult(Response(HttpStatusCode.OK, payload));
        });

        var details = await test.Client.GetDetailsAsync(Account, "octo/tool", 7, TestContext.CancellationToken);

        Assert.AreEqual("src/login.cs", details.Files.Single().FileName);
        Assert.AreEqual("APPROVED", details.Reviews.Single().State);
        Assert.AreEqual("CI", details.Checks.Single().Name);
        Assert.AreEqual("pending", details.CommitStatus);
        Assert.AreEqual(PullRequestChecksState.Pending, details.ChecksState);
        Assert.IsNull(details.FilesError);
        Assert.IsNull(details.ReviewsError);
        Assert.IsNull(details.ChecksError);
        Assert.IsNull(details.StatusError);
    }

    [TestMethod]
    public async Task GetDetails_FailedChecksAreUnknownRatherThanPassing()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/check-runs", StringComparison.Ordinal) || path.EndsWith("/status", StringComparison.Ordinal))
                return Task.FromResult(Response(HttpStatusCode.InternalServerError, "{}"));
            var payload = path.EndsWith("/pulls/7", StringComparison.Ordinal)
                ? Pull(HeadSha)
                : path.EndsWith("/pulls/7/files", StringComparison.Ordinal) || path.EndsWith("/pulls/7/reviews", StringComparison.Ordinal)
                    ? "[]"
                    : """{"permissions":{"push":true}}""";
            return Task.FromResult(Response(HttpStatusCode.OK, payload));
        });

        var details = await test.Client.GetDetailsAsync(Account, "octo/tool", 7, TestContext.CancellationToken);

        Assert.AreEqual(PullRequestChecksState.Unknown, details.ChecksState);
        Assert.IsNotNull(details.ChecksError);
        Assert.IsNotNull(details.StatusError);
    }

    [TestMethod]
    public async Task GetDetails_CheckTimeoutIsPartialFailureAndDoesNotHideOtherSections()
    {
        using var test = new FeatureClientBundle((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/check-runs", StringComparison.Ordinal))
                return Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"));
            var payload = path.EndsWith("/pulls/7", StringComparison.Ordinal) ? Pull(HeadSha)
                : path.EndsWith("/pulls/7/files", StringComparison.Ordinal)
                    ? """[{"filename":"src/login.cs","status":"modified","additions":5,"deletions":2}]"""
                : path.EndsWith("/pulls/7/reviews", StringComparison.Ordinal) ? "[]"
                : path.EndsWith("/status", StringComparison.Ordinal) ? """{"state":"success"}"""
                : """{"permissions":{"push":true}}""";
            return Task.FromResult(Response(HttpStatusCode.OK, payload));
        });

        var details = await test.Client.GetDetailsAsync(Account, "octo/tool", 7, TestContext.CancellationToken);

        Assert.AreEqual("src/login.cs", details.Files.Single().FileName);
        Assert.IsNotNull(details.ChecksError);
        Assert.IsNull(details.StatusError);
        Assert.AreEqual("success", details.CommitStatus);
        Assert.AreEqual(PullRequestChecksState.Unknown, details.ChecksState);
    }

    [TestMethod]
    public async Task GetDetails_PaginatesFilesAndReviews()
    {
        var filesPageTwo = new Uri("https://api.github.com/repos/octo/tool/pulls/7/files?per_page=100&page=2");
        var reviewsPageTwo = new Uri("https://api.github.com/repos/octo/tool/pulls/7/reviews?per_page=100&page=2");
        using var test = new FeatureClientBundle((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/pulls/7/files", StringComparison.Ordinal))
            {
                var pageTwo = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                return Task.FromResult(Response(HttpStatusCode.OK,
                    pageTwo ? """[{"filename":"second.cs","status":"modified","additions":2,"deletions":0}]"""
                        : """[{"filename":"first.cs","status":"modified","additions":1,"deletions":0}]""",
                    pageTwo ? null : filesPageTwo));
            }
            if (path.EndsWith("/pulls/7/reviews", StringComparison.Ordinal))
            {
                var pageTwo = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal);
                return Task.FromResult(Response(HttpStatusCode.OK,
                    pageTwo ? "[" + Review(2, "COMMENTED", HeadSha, "Second review") + "]"
                        : "[" + Review(1, "APPROVED", HeadSha, "First review") + "]",
                    pageTwo ? null : reviewsPageTwo));
            }
            var payload = path.EndsWith("/pulls/7", StringComparison.Ordinal) ? Pull(HeadSha)
                : path.EndsWith("/check-runs", StringComparison.Ordinal) ? """{"check_runs":[]}"""
                : path.EndsWith("/status", StringComparison.Ordinal) ? """{"state":"success"}"""
                : """{"permissions":{"push":true}}""";
            return Task.FromResult(Response(HttpStatusCode.OK, payload));
        });

        var details = await test.Client.GetDetailsAsync(Account, "octo/tool", 7, TestContext.CancellationToken);

        Assert.AreEqual("first.cs,second.cs", string.Join(",", details.Files.Select(file => file.FileName)));
        Assert.AreEqual("1,2", string.Join(",", details.Reviews.Select(review => review.Id)));
        Assert.AreEqual(2, test.Handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)));
        Assert.AreEqual(2, test.Handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/reviews", StringComparison.Ordinal)));
    }

    private const string HeadSha = "head-sha-123";

    private static string Pull(string sha, bool draft = false, string author = "contributor") =>
        "{\"number\":7,\"node_id\":\"PR_7\",\"title\":\"Improve login\",\"body\":\"Description\","
        + "\"html_url\":\"https://github.com/octo/tool/pull/7\",\"state\":\"open\",\"draft\":"
        + draft.ToString().ToLowerInvariant()
        + ",\"user\":{\"login\":" + GitHubJson.String(author) + "},\"head\":{\"sha\":" + GitHubJson.String(sha)
        + ",\"ref\":\"feature\"},\"base\":{\"sha\":\"base-sha\",\"ref\":\"main\"}}";

    private static string Review(int id, string state, string commit, string body) =>
        "{\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + ",\"state\":" + GitHubJson.String(state) + ",\"commit_id\":" + GitHubJson.String(commit)
        + ",\"body\":" + GitHubJson.String(body)
        + ",\"user\":{\"login\":\"octocat\"},\"submitted_at\":\"2025-06-01T11:00:00Z\"}";

    private static HttpResponseMessage Response(HttpStatusCode status, string body, Uri? next = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (next is not null) response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
        return response;
    }

    private sealed class FeatureClientBundle : IDisposable
    {
        internal FeatureClientBundle(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        {
            Handler = new FeatureHandler(respond);
            Http = new HttpClient(Handler);
            Client = new PullRequestActionsClient(Http);
        }

        internal FeatureHandler Handler { get; }
        private HttpClient Http { get; }
        internal PullRequestActionsClient Client { get; }
        public void Dispose() => Http.Dispose();
    }

    private sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? Body);

    private sealed class FeatureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        internal List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
                Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, body));
            return await respond(request, cancellationToken);
        }
    }
}
