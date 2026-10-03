// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using static BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues.IssueMutationTestData;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueManagementClientTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task GetMilestones_FollowsValidatedPagination()
    {
        var next = new Uri("https://api.github.com/repos/octo/tool/milestones?state=all&per_page=100&page=2");
        using var handler = new IssueTestHandler((_, _, _) =>
            Task.FromResult(Response("""[{"number":3,"title":"June","state":"open","open_issues":4}]""", next: next)));
        using var http = new HttpClient(handler);
        var client = new IssueManagementClient(http);

        var first = await client.GetMilestonesAsync(Account, "octo/tool", null, TestContext.CancellationToken);
        var second = await client.GetMilestonesAsync(Account, "octo/tool", first.NextPage, TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/repos/octo/tool/milestones?state=all&per_page=100"), handler.Requests[0].Uri);
        Assert.AreEqual(next, first.NextPage);
        Assert.AreEqual("June", first.Milestones.Single().Title);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual("June", second.Milestones.Single().Title);
    }

    [TestMethod]
    public async Task CreateIssue_UsesSourceGeneratedPayloadAndIncludesNullMilestone()
    {
        const string response = """
            {"number":42,"title":"New issue","body":"Details","state":"open",
             "html_url":"https://github.com/octo/tool/issues/42","created_at":"2025-06-01T11:00:00Z"}
            """;
        using var handler = new IssueTestHandler((request, _, _) => Task.FromResult(Response(
            request.Method == HttpMethod.Get
                ? """{"number":5,"title":"June","state":"open","open_issues":1}"""
                : response)));
        using var http = new HttpClient(handler);

        var issue = await new IssueManagementClient(http).CreateIssueAsync(
            Account, "octo/tool", " New issue ", "Details", null, TestContext.CancellationToken);

        var request = Assert.ContainsSingle(handler.Requests);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual(new Uri("https://api.github.com/repos/octo/tool/issues"), request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.AreEqual("New issue", body.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("Details", body.RootElement.GetProperty("body").GetString());
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("milestone").ValueKind);
        Assert.IsNull(issue.MilestoneNumber);
    }

    [TestMethod]
    public async Task CreateIssue_MalformedSuccessIsOutcomeUnknown()
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("{}")));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new IssueManagementClient(http)
            .CreateIssueAsync(Account, "octo/tool", "New issue", null, null, TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task CreateIssue_RejectsSilentlyIgnoredFieldsAsUnknownOutcome()
    {
        const string response = """
            {"number":42,"title":"New issue","body":"Details","state":"open",
             "html_url":"https://github.com/octo/tool/issues/42","created_at":"2025-06-01T11:00:00Z"}
            """;
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(response)));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new IssueManagementClient(http)
            .CreateIssueAsync(Account, "octo/tool", "New issue", "Details", 5, TestContext.CancellationToken));

        Assert.IsTrue(error.OutcomeUnknown);
        Assert.Contains("didn't save every issue field", error.Message);
    }

    [TestMethod]
    public async Task UpdateIssue_RequiresUnchangedTextAndMilestoneBeforePatch()
    {
        var current = """
            {"number":42,"title":"Keyboard navigation","body":"Description","state":"open",
             "html_url":"https://github.com/octo/tool/issues/42","created_at":"2025-06-01T11:00:00Z",
             "milestone":{"number":6}}
            """;
        using var handler = new IssueTestHandler((request, _, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Response(current));
            return Task.FromResult(Response(current));
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new IssueManagementClient(http).UpdateIssueAsync(
            Account, "octo/tool", Issue(), "Revised", "New description", null, TestContext.CancellationToken));

        Assert.Contains("changed since you opened", error.Message);
        Assert.AreEqual(HttpMethod.Get, Assert.ContainsSingle(handler.Requests).Method);
    }

    [TestMethod]
    public async Task UpdateIssue_ClearsMilestoneWhenIssueIsUnchanged()
    {
        const string original = """
            {"number":42,"title":"Keyboard navigation","body":"Description","state":"open",
             "html_url":"https://github.com/octo/tool/issues/42","created_at":"2025-06-01T11:00:00Z",
             "milestone":{"number":6}}
            """;
        const string updated = """
            {"number":42,"title":"Revised","body":"New description","state":"open",
             "html_url":"https://github.com/octo/tool/issues/42","created_at":"2025-06-01T11:00:00Z"}
            """;
        using var handler = new IssueTestHandler((request, _, _) =>
            Task.FromResult(Response(request.Method == HttpMethod.Get ? original : updated)));
        using var http = new HttpClient(handler);
        var expected = Issue() with { MilestoneNumber = 6 };

        var issue = await new IssueManagementClient(http).UpdateIssueAsync(
            Account, "octo/tool", expected, "Revised", "New description", null, TestContext.CancellationToken);

        var patch = handler.Requests.Single(request => request.Method == HttpMethod.Patch);
        using var body = JsonDocument.Parse(patch.Body);
        Assert.AreEqual(JsonValueKind.Null, body.RootElement.GetProperty("milestone").ValueKind);
        Assert.IsNull(issue.MilestoneNumber);
    }
}
