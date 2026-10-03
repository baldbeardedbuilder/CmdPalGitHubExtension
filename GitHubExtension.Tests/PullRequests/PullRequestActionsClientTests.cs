// Copyright (c) BaldBeardedBuilder LLC
// BaldBeardedBuilder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class PullRequestActionsClientTests
{
    private const string Repository = """{"permissions":{"push":true,"triage":true}}""";
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");

    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task Close_RechecksPermissionsAndRequiresOpenUnmergedPullRequest()
    {
        using var denied = CreateClient(
            (HttpStatusCode.OK, """{"permissions":{"push":false,"triage":false}}"""),
            (HttpStatusCode.OK, """{"state":"open"}"""));
        var permissionError = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            denied.Client.SetStateAsync(Account, "octo/tool", 42, open: false, TestContext.CancellationToken));
        Assert.Contains("Write access", permissionError.Message);
        Assert.IsTrue(denied.Handler.Requests.All(request => request.Method == HttpMethod.Get));

        using var stale = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, """{"state":"closed"}"""));
        var staleError = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            stale.Client.SetStateAsync(Account, "octo/tool", 42, open: false, TestContext.CancellationToken));
        Assert.Contains("state changed", staleError.Message);
        Assert.IsTrue(stale.Handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task Reopen_MergedPullRequestIsRejectedUsingMergedAt()
    {
        using var test = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, """{"state":"closed","merged_at":"2026-01-02T00:00:00Z"}"""));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            test.Client.SetStateAsync(Account, "octo/tool", 42, open: true, TestContext.CancellationToken));

        Assert.Contains("Merged pull requests", error.Message);
        Assert.IsTrue(test.Handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task CloseAndReopen_UseReturnedStateWithoutTreatingClosureAsMerge()
    {
        using var close = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, """{"state":"open","merged":false}"""),
            (HttpStatusCode.OK, """{"state":"closed","merged":false,"merged_at":null}"""));
        await close.Client.SetStateAsync(Account, "octo/tool", 42, open: false, TestContext.CancellationToken);
        Assert.AreEqual(HttpMethod.Patch, close.Handler.Requests.Last().Method);
        using (var body = JsonDocument.Parse(close.Handler.Requests.Last().Body!))
        {
            Assert.AreEqual("closed", body.RootElement.GetProperty("state").GetString());
        }

        using var reopen = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, """{"state":"closed","merged":false}"""),
            (HttpStatusCode.OK, """{"state":"open","merged":false,"merged_at":null}"""));
        await reopen.Client.SetStateAsync(Account, "octo/tool", 42, open: true, TestContext.CancellationToken);
        using var reopenBody = JsonDocument.Parse(reopen.Handler.Requests.Last().Body!);
        Assert.AreEqual("open", reopenBody.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task ReviewerRequest_PreservesOtherRequestsAndRejectsIgnoredReviewer()
    {
        using var added = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(requested: """[{"login":"existing"}]""")),
            (HttpStatusCode.OK, """{"requested_reviewers":[{"login":"existing"},{"login":"new-reviewer"}],"requested_teams":[]}"""));
        await added.Client.SetReviewerAsync(Account, "octo/tool", 42, "new-reviewer", team: false, add: true, TestContext.CancellationToken);
        Assert.AreEqual(HttpMethod.Post, added.Handler.Requests.Last().Method);
        using (var body = JsonDocument.Parse(added.Handler.Requests.Last().Body!))
        {
            Assert.AreEqual("new-reviewer", body.RootElement.GetProperty("reviewers").EnumerateArray().Single().GetString());
        }

        using var ignored = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull()),
            (HttpStatusCode.OK, """{"requested_reviewers":[],"requested_teams":[]}"""));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            ignored.Client.SetReviewerAsync(Account, "octo/tool", 42, "invalid", team: false, add: true, TestContext.CancellationToken));
        Assert.Contains("did not confirm", error.Message);
    }

    [TestMethod]
    public async Task RemoveReviewerAndAssignee_OnlyRemoveSelectedIdentity()
    {
        using var reviewer = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(requested: """[{"login":"remove-me"},{"login":"keep-me"}]""")),
            (HttpStatusCode.OK, """{"requested_reviewers":[{"login":"keep-me"}],"requested_teams":[]}"""));
        await reviewer.Client.SetReviewerAsync(Account, "octo/tool", 42, "remove-me", team: false, add: false, TestContext.CancellationToken);
        Assert.AreEqual(HttpMethod.Delete, reviewer.Handler.Requests.Last().Method);
        using (var body = JsonDocument.Parse(reviewer.Handler.Requests.Last().Body!))
        {
            Assert.AreEqual("remove-me", body.RootElement.GetProperty("reviewers").EnumerateArray().Single().GetString());
        }

        using var assignee = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(assignees: """[{"login":"remove-me"},{"login":"keep-me"}]""")),
            (HttpStatusCode.OK, """{"assignees":[{"login":"keep-me"}]}"""));
        await assignee.Client.SetAssigneeAsync(Account, "octo/tool", 42, "remove-me", add: false, TestContext.CancellationToken);
        Assert.AreEqual(HttpMethod.Delete, assignee.Handler.Requests.Last().Method);
        using var assigneeBody = JsonDocument.Parse(assignee.Handler.Requests.Last().Body!);
        Assert.AreEqual("remove-me", assigneeBody.RootElement.GetProperty("assignees").EnumerateArray().Single().GetString());
    }

    [TestMethod]
    public async Task ReviewerChanges_RejectClosedAndMergedPullRequests()
    {
        foreach (var payload in new[]
        {
            """{"state":"closed","merged":false}""",
            """{"state":"closed","merged_at":"2026-01-02T00:00:00Z"}""",
        })
        {
            using var test = CreateClient(
                (HttpStatusCode.OK, Repository),
                (HttpStatusCode.OK, payload));
            var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
                test.Client.SetReviewerAsync(Account, "octo/tool", 42, "reviewer", team: false, add: true, TestContext.CancellationToken));
            Assert.Contains("Only open", error.Message);
            Assert.IsTrue(test.Handler.Requests.All(request => request.Method == HttpMethod.Get));
        }
    }

    [TestMethod]
    public async Task Assignment_UsesAdditiveIssueEndpointAndRejectsIgnoredAssignment()
    {
        using var added = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(assignees: """[{"login":"existing"}]""")),
            (HttpStatusCode.OK, """{"assignees":[{"login":"existing"},{"login":"new-assignee"}]}"""));
        await added.Client.SetAssigneeAsync(Account, "octo/tool", 42, "new-assignee", add: true, TestContext.CancellationToken);
        Assert.EndsWith("/issues/42/assignees", added.Handler.Requests.Last().Url);
        Assert.AreEqual(HttpMethod.Post, added.Handler.Requests.Last().Method);
        using (var body = JsonDocument.Parse(added.Handler.Requests.Last().Body!))
        {
            Assert.AreEqual("new-assignee", body.RootElement.GetProperty("assignees").EnumerateArray()
                .Single(value => value.GetString() == "new-assignee").GetString());
        }

        using var ignored = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(assignees: """[{"login":"existing"}]""")),
            (HttpStatusCode.OK, """{"assignees":[{"login":"existing"}]}"""));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            ignored.Client.SetAssigneeAsync(Account, "octo/tool", 42, "ignored", add: true, TestContext.CancellationToken));
        Assert.Contains("did not confirm", error.Message);
    }

    [TestMethod]
    public async Task LabelChanges_PreserveExistingLabelsAndEscapeSpecialNames()
    {
        const string special = "needs: triage/urgent";
        using var added = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(labels: """[{"name":"existing"}]""")),
            (HttpStatusCode.OK, """[{"name":"existing"},{"name":"needs: triage/urgent"}]"""));
        await added.Client.SetLabelAsync(Account, "octo/tool", 42, special, add: true, TestContext.CancellationToken);
        Assert.AreEqual(HttpMethod.Post, added.Handler.Requests.Last().Method);
        using (var body = JsonDocument.Parse(added.Handler.Requests.Last().Body!))
        {
            Assert.AreEqual(special, body.RootElement.GetProperty("labels")[0].GetString());
        }

        using var removed = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(labels: """[{"name":"existing"},{"name":"needs: triage/urgent"}]""")),
            (HttpStatusCode.OK, """{"name":"needs: triage/urgent"}"""));
        await removed.Client.SetLabelAsync(Account, "octo/tool", 42, special, add: false, TestContext.CancellationToken);
        Assert.EndsWith("/labels/needs%3A%20triage%2Furgent", removed.Handler.Requests.Last().Url);
        Assert.AreEqual(HttpMethod.Delete, removed.Handler.Requests.Last().Method);
    }

    [TestMethod]
    public async Task Mutations_RequireAppropriateRepositoryPermissionAndRejectIgnoredLabel()
    {
        using var reviewer = CreateClient(
            (HttpStatusCode.OK, """{"permissions":{"push":false,"triage":true}}"""),
            (HttpStatusCode.OK, Pull()));
        var reviewerError = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            reviewer.Client.SetReviewerAsync(Account, "octo/tool", 42, "reviewer", team: false, add: true, TestContext.CancellationToken));
        Assert.Contains("Write access", reviewerError.Message);
        Assert.IsTrue(reviewer.Handler.Requests.All(request => request.Method == HttpMethod.Get));

        using var label = CreateClient(
            (HttpStatusCode.OK, Repository),
            (HttpStatusCode.OK, Pull(labels: """[{"name":"existing"}]""")),
            (HttpStatusCode.OK, """[{"name":"existing"}]"""));
        var labelError = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            label.Client.SetLabelAsync(Account, "octo/tool", 42, "ignored", add: true, TestContext.CancellationToken));
        Assert.Contains("did not confirm", labelError.Message);
    }

    [TestMethod]
    public async Task AssigneeAndLabelChanges_RequireTriagePermission()
    {
        using var assignee = CreateClient(
            (HttpStatusCode.OK, """{"permissions":{"push":false,"triage":false}}"""),
            (HttpStatusCode.OK, Pull()));
        var assigneeError = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            assignee.Client.SetAssigneeAsync(Account, "octo/tool", 42, "mona", add: true, TestContext.CancellationToken));
        Assert.Contains("Triage or write access", assigneeError.Message);
        Assert.IsTrue(assignee.Handler.Requests.All(request => request.Method == HttpMethod.Get));

        using var label = CreateClient(
            (HttpStatusCode.OK, """{"permissions":{"push":false,"triage":false}}"""),
            (HttpStatusCode.OK, Pull()));
        var labelError = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            label.Client.SetLabelAsync(Account, "octo/tool", 42, "bug", add: true, TestContext.CancellationToken));
        Assert.Contains("Triage or write access", labelError.Message);
        Assert.IsTrue(label.Handler.Requests.All(request => request.Method == HttpMethod.Get));
    }

    [TestMethod]
    public async Task Options_OfferValidReviewersTeamsAssigneesAndLabels()
    {
        using var test = CreateClient(
            (HttpStatusCode.OK, """{"owner":{"type":"Organization"}}"""),
            (HttpStatusCode.OK, """[{"login":"octocat"}]"""),
            (HttpStatusCode.OK, """[{"login":"mona"}]"""),
            (HttpStatusCode.OK, """[{"name":"needs: triage"}]"""),
            (HttpStatusCode.OK, """[{"slug":"design-review"}]"""));

        var options = await test.Client.GetOptionsAsync(Account, "octo/tool", TestContext.CancellationToken);

        Assert.AreEqual("octocat", options.Reviewers.Single());
        Assert.AreEqual("design-review", options.Teams.Single());
        Assert.AreEqual("mona", options.Assignees.Single());
        Assert.AreEqual("needs: triage", options.Labels.Single());
    }

    [TestMethod]
    public async Task AccountSwitch_InvalidatesConfirmationBeforeMutation()
    {
        var client = new Mock<IPullRequestActionsClient>();
        client.Setup(c => c.GetSnapshotAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestActionSnapshot(SubjectState.Open, false, true, true, [], [], [], []));
        client.Setup(c => c.GetOptionsAsync(Account, "octo/tool", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestActionOptions(["octocat"], [], ["octocat"], ["bug"]));
        var authClient = new Mock<IGitHubAuthClient>();
        authClient.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account.Login);
        var auth = new AuthService(new InMemoryAccountStore(Account), authClient.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestActionsPage(auth, client.Object, Account, "octo/tool", 42,
            new Uri("https://github.com/octo/tool/pull/42"));
        page.GetContent();
        await page.CurrentWork;

        page.HandleSubmit("{}", """{"action":"close"}""");
        var confirmation = ((IFormContent)page.GetContent()[0]).TemplateJson;
        using var template = JsonDocument.Parse(confirmation);
        var confirmData = template.RootElement.GetProperty("body").EnumerateArray()
            .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "ActionSet")
            .SelectMany(element => element.GetProperty("actions").EnumerateArray())
            .Single(action => action.GetProperty("title").GetString() == "Confirm")
            .GetProperty("data").GetRawText();
        await auth.SignInWithTokenAsync("github.com", "replacement-token", CancellationToken.None);
        page.HandleSubmit("{}", confirmData);
        await page.CurrentWork;

        Assert.Contains("account changed", ((IFormContent)page.GetContent()[0]).TemplateJson);
        client.Verify(c => c.SetStateAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task SignOutDuringChoiceLoad_CancelsRequestAndSkipsRemainingLoads()
    {
        var client = new Mock<IPullRequestActionsClient>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetSnapshotAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, int _, CancellationToken token) =>
            {
                started.SetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new PullRequestActionSnapshot(SubjectState.Open, false, true, true, [], [], [], []);
            });
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestActionsPage(auth, client.Object, Account, "octo/tool", 42,
            new Uri("https://github.com/octo/tool/pull/42"));
        page.GetContent();
        var token = await started.Task;

        auth.SignOut();
        await page.CurrentWork;

        Assert.IsTrue(token.IsCancellationRequested);
        client.Verify(c => c.GetOptionsAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("account changed", ((IFormContent)page.GetContent()[0]).TemplateJson);
    }

    private static string Pull(string? requested = null, string? assignees = null, string? labels = null) =>
        $$"""{"state":"open","merged":false,"requested_reviewers":{{requested ?? "[]"}},"requested_teams":[],"assignees":{{assignees ?? "[]"}},"labels":{{labels ?? "[]"}}}""";

    private static ClientBundle CreateClient(params (HttpStatusCode Status, string Json)[] responses)
    {
        var handler = new StubHandler(responses);
        var http = new HttpClient(handler);
        return new(new PullRequestActionsClient(http), handler, http);
    }

    private sealed record ClientBundle(PullRequestActionsClient Client, StubHandler Handler, HttpClient Http) : IDisposable
    {
        public void Dispose()
        {
            Http.Dispose();
            Handler.Dispose();
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Url, string? Body);

    private sealed class StubHandler((HttpStatusCode Status, string Json)[] responses) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.Method, request.RequestUri!.AbsoluteUri, body));
            var response = responses[Requests.Count - 1];
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
