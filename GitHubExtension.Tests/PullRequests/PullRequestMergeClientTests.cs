// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class PullRequestMergeClientTests
{
    private const string Uuid = "12345678-1234-1234-1234-123456789abc";
    private const string RepositoryJson = """{"permissions":{"push":true},"allow_squash_merge":true,"allow_merge_commit":false,"allow_rebase_merge":true}""";
    private const string PullJson = """{"state":"open","draft":false,"head":{"sha":"abc123"},"base":{"ref":"main"},"mergeable":true}""";
    private const string PendingJson = $$$"""{"status":"pending","details":{"uuid":"{{{Uuid}}}","message":"Working","expected_head_sha":"abc123","merge_method":"squash","merge_action":"default","bypass_rules":false}}""";
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly PullRequestMergeTarget Target = new("octo/tool", 42, "main", "abc123", ["squash", "rebase"]);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Merge_RevalidatesScopeAndSendsConfirmedOptionsWithoutBypass()
    {
        using var handler = Handler((HttpStatusCode.Accepted, PendingJson));
        using var http = new HttpClient(handler);
        var client = new PullRequestMergeClient(http);

        var result = await client.MergeAsync(Account, Target, "squash", TestContext.CancellationToken);

        Assert.AreEqual("pending", result.Status);
        Assert.AreEqual(Uuid, result.Uuid);
        Assert.AreEqual(3, handler.Requests.Count);
        var request = handler.Requests.Last();
        Assert.AreEqual(HttpMethod.Put, request.Method);
        Assert.AreEqual("https://api.github.com/repos/octo/tool/pulls/42/merge-async", request.Url);
        Assert.AreEqual("Bearer " + Account.Token, request.Auth);
        Assert.AreEqual("2022-11-28", request.Version);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual(Target.HeadSha, body.RootElement.GetProperty("sha").GetString());
        Assert.AreEqual("squash", body.RootElement.GetProperty("merge_method").GetString());
        Assert.AreEqual("default", body.RootElement.GetProperty("merge_action").GetString());
        Assert.IsFalse(body.RootElement.GetProperty("bypass_rules").GetBoolean());
    }

    [TestMethod]
    [DataRow("abc123", "other")]
    [DataRow("new-sha", "main")]
    public async Task ChangedHeadOrBase_StopsBeforeMutation(string sha, string branch)
    {
        using var handler = new StubHandler(
            (HttpStatusCode.OK, RepositoryJson),
            (HttpStatusCode.OK, $$$"""{"state":"open","head":{"sha":"{{{sha}}}"},"base":{"ref":"{{{branch}}}"}}"""));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken));

        Assert.IsTrue(handler.Requests.All(r => r.Method == HttpMethod.Get));
    }

    [TestMethod]
    [DataRow("""{"state":"open","stack":true,"head":{"sha":"abc123"},"base":{"ref":"main"}}""", "stack scope")]
    [DataRow("""{"state":"open","mergeable":false}""", "conflicts")]
    [DataRow("""{"state":"closed"}""", "open")]
    [DataRow("""{"state":"open","draft":true}""", "non-draft")]
    [DataRow("""{"state":"open","head":{},"base":{}}""", "head SHA")]
    public async Task UnsafeTarget_RefusesMerge(string pull, string expectedMessage)
    {
        using var handler = new StubHandler((HttpStatusCode.OK, RepositoryJson), (HttpStatusCode.OK, pull));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken));

        Assert.Contains(expectedMessage, error.Message);
        Assert.IsTrue(handler.Requests.All(r => r.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task StackSnapshot_IsConfirmedAndChangePreventsSubmission()
    {
        var stack = """{"id":5,"position":2,"size":3,"base":{"ref":"main","sha":"base-sha"}}""";
        var pull = PullJson[..^1] + $",\"stack\":{stack}}}";
        using var handler = new StubHandler(
            (HttpStatusCode.OK, RepositoryJson), (HttpStatusCode.OK, pull),
            (HttpStatusCode.OK, RepositoryJson), (HttpStatusCode.OK, pull),
            (HttpStatusCode.Accepted, PendingJson),
            (HttpStatusCode.OK, RepositoryJson), (HttpStatusCode.OK, PullJson));
        using var http = new HttpClient(handler);
        var client = new PullRequestMergeClient(http);
        var target = await client.GetTargetAsync(Account, "octo/tool", 42, TestContext.CancellationToken);
        Assert.AreEqual(stack, target.StackScope);
        var result = await client.MergeAsync(Account, target, "squash", TestContext.CancellationToken);
        Assert.AreEqual("pending", result.Status);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.MergeAsync(Account, target, "squash", TestContext.CancellationToken));

        Assert.Contains("stack scope", error.Message);
        Assert.AreEqual(1, handler.Requests.Count(r => r.Method == HttpMethod.Put));
    }

    [TestMethod]
    public async Task NewlyStackedPr_StopsBeforeMutation()
    {
        var pull = PullJson[..^1] + ",\"stack\":{\"id\":5,\"position\":2}}";
        using var handler = new StubHandler((HttpStatusCode.OK, RepositoryJson), (HttpStatusCode.OK, pull));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken));
        Assert.Contains("stack scope", error.Message);
        Assert.IsTrue(handler.Requests.All(r => r.Method == HttpMethod.Get));
    }

    [TestMethod]
    [DataRow("""{"permissions":{"push":false},"allow_squash_merge":true}""")]
    [DataRow("""{"permissions":{"push":true},"allow_squash_merge":false}""")]
    [DataRow("""{}""")]
    public async Task PermissionsOrMethodsUnavailable_StopsBeforeMutation(string repo)
    {
        using var handler = new StubHandler((HttpStatusCode.OK, repo));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken));

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task DisabledMethod_DoesNotSendRequest()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "merge", TestContext.CancellationToken));
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    [DataRow("enqueued", """{"message":"Queued"}""")]
    [DataRow("failed", """{"message":"Required checks failed"}""")]
    [DataRow("merged", """{"message":"Merged","sha":"merge-sha"}""")]
    public async Task StatusLookup_UsesUuidAndKeepsOutcomesDistinct(string status, string details)
    {
        using var handler = new StubHandler((HttpStatusCode.OK, $$"""{"status":"{{status}}","details":{{details}}}"""));
        using var http = new HttpClient(handler);
        var result = await new PullRequestMergeClient(http).GetStatusAsync(Account, Target, Uuid, TestContext.CancellationToken);

        Assert.AreEqual(status, result.Status);
        Assert.IsNull(result.Uuid);
        Assert.AreEqual($"https://api.github.com/repos/octo/tool/pulls/42/merge-async/{Uuid}", handler.Requests.Single().Url);
        Assert.AreEqual(HttpMethod.Get, handler.Requests.Single().Method);
        if (status == "enqueued") Assert.Contains("not merged", result.Summary);
        if (status == "failed") Assert.Contains("Required checks failed", result.Summary);
    }

    [TestMethod]
    public async Task ExistingPendingRequest_IsClearlyIdentified()
    {
        using var handler = Handler((HttpStatusCode.Conflict, PendingJson));
        using var http = new HttpClient(handler);
        var result = await new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken);
        Assert.Contains("existing request", result.Summary);
        Assert.AreEqual(Uuid, result.Uuid);
        Assert.Contains("abc123", result.Summary);
    }

    [TestMethod]
    [DataRow(404)]
    [DataRow(405)]
    [DataRow(501)]
    [DataRow(403)]
    [DataRow(422)]
    public async Task RejectedOrUnsupportedApi_NeverFallsBack(int status)
    {
        using var handler = Handler(((HttpStatusCode)status, "{}"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken));
        Assert.HasCount(3, handler.Requests);
        Assert.EndsWith("/merge-async", handler.Requests.Last().Url);
    }

    [TestMethod]
    public async Task EnterpriseSupportUnverified_StopsWithoutRequest()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(new(host, "octocat", "test-token"), Target, "squash", TestContext.CancellationToken));
        Assert.Contains("not verified", error.Message);
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task TimeoutAfterSubmission_ReportsUnknownOutcomeWithoutRetry()
    {
        using var handler = Handler((HttpStatusCode.Accepted, PendingJson));
        handler.TimeoutPut = true;
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new PullRequestMergeClient(http).MergeAsync(Account, Target, "squash", TestContext.CancellationToken));
        Assert.Contains("outcome is unknown", error.Message);
        Assert.HasCount(3, handler.Requests);
    }

    [TestMethod]
    [DataRow("""{"status":"success","details":{}}""")]
    [DataRow("""{"status":"merged","details":{"message":"Not enough evidence"}}""")]
    [DataRow("""{"status":"pending","details":{"uuid":"invalid"}}""")]
    [DataRow("[]")]
    public void MalformedResult_NeverClaimsMerged(string text)
    {
        using var json = JsonDocument.Parse(text);
        Assert.ThrowsExactly<GitHubApiException>(() => PullRequestMergeClient.ParseResult(json.RootElement));
    }

    private static StubHandler Handler((HttpStatusCode, string) result) =>
        new((HttpStatusCode.OK, RepositoryJson), (HttpStatusCode.OK, PullJson), result);

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? Auth, string Version, string? Body);

    private sealed class StubHandler(params (HttpStatusCode Status, string Json)[] responses) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public bool TimeoutPut { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Headers.GetValues("X-GitHub-Api-Version").Single(),
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (TimeoutPut && request.Method == HttpMethod.Put) throw new TaskCanceledException();
            var response = responses[Requests.Count - 1];
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
