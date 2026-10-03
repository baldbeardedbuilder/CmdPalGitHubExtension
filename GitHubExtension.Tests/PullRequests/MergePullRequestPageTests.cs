// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class MergePullRequestPageTests
{
    private const string Uuid = "12345678-1234-1234-1234-123456789abc";
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly PullRequestMergeTarget Target = new("octo/tool", 42, "main", "abc123", ["squash"]);

    [TestMethod]
    public async Task Confirmation_ShowsAccountTargetShaMethodScopeAndQueueSemantics()
    {
        var client = Client();
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        var template = Template(page);
        Assert.Contains("octo/tool#42", template);
        Assert.Contains("main", template);
        Assert.Contains("abc123", template);
        Assert.Contains("octocat@github.com", template);
        Assert.Contains("ALL open downstack PRs", template);
        Assert.Contains("not the target branch or downstack scope", template);
        Assert.Contains("queue controls its merge method", template);
        Assert.Contains("never bypassed", template);
        Assert.Contains("squash", template);
        client.Verify(c => c.MergeAsync(It.IsAny<GitHubAccount>(), It.IsAny<PullRequestMergeTarget>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Confirm_SubmitsOnceAndStatusUsesGetOnly()
    {
        var client = Client();
        client.Setup(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestMergeResult("pending", Uuid, "Pending, not completed"));
        client.Setup(c => c.GetStatusAsync(Account, Target, Uuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestMergeResult("enqueued", null, "Enqueued, not merged"));
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        var confirmation = ActionData(page, "confirm");
        Submit(page, confirmation);
        Submit(page, confirmation);
        await page.CurrentWork;
        Assert.Contains("Pending", Template(page));

        Submit(page, ActionData(page, "status"));
        await page.CurrentWork;

        Assert.Contains("Enqueued, not merged", Template(page));
        Assert.DoesNotContain("Check status", Template(page));
        client.Verify(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetStatusAsync(Account, Target, Uuid, It.IsAny<CancellationToken>()), Times.Once);
        Submit(page, confirmation);
        client.Verify(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CancelBeforeConfirmation_NeverMutates()
    {
        var client = Client();
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        var confirmation = ActionData(page, "confirm");
        Submit(page, """{"action":"cancel"}""");
        Submit(page, confirmation);
        await page.CurrentWork;
        Assert.Contains("not cancelled", Template(page));
        VerifyNoMerge(client);
    }

    [TestMethod]
    public async Task TimeoutOrPermissionFailure_DoesNotOfferMutationRetry()
    {
        var client = Client();
        client.Setup(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("The request timed out or was denied."));
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        var confirmation = ActionData(page, "confirm");
        Submit(page, confirmation);
        await page.CurrentWork;
        Assert.Contains("No completion is confirmed", Template(page));
        Assert.DoesNotContain("Confirm merge", Template(page));
        Submit(page, confirmation);
        client.Verify(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task FailedStatusLookup_KeepsUuidForReadOnlyRetry()
    {
        var client = Client();
        client.Setup(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestMergeResult("pending", Uuid, "Pending"));
        client.SetupSequence(c => c.GetStatusAsync(Account, Target, Uuid, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Timed out"))
            .ReturnsAsync(new PullRequestMergeResult("merged", null, "Merged"));
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        Submit(page, ActionData(page, "confirm"));
        await page.CurrentWork;
        Submit(page, ActionData(page, "status"));
        await page.CurrentWork;
        Assert.Contains("Timed out", Template(page));
        Submit(page, ActionData(page, "status"));
        await page.CurrentWork;
        Assert.Contains("Merged", Template(page));
        client.Verify(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AccountSwitch_InvalidatesOldConfirmationEvenForSameLogin()
    {
        var client = Client();
        using var page = Page(client.Object, out var auth);
        page.GetContent();
        await page.CurrentWork;
        var confirmation = ActionData(page, "confirm");
        await auth.SignInWithTokenAsync("github.com", "replacement-token", CancellationToken.None);
        Submit(page, confirmation);
        await page.CurrentWork;
        Assert.Contains("account changed", Template(page));
        Assert.DoesNotContain("abc123", Template(page));
        VerifyNoMerge(client);
    }

    [TestMethod]
    public async Task SignOutDuringMutation_CancelsLocallyAndDiscardsLateResult()
    {
        var client = Client();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<PullRequestMergeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, PullRequestMergeTarget _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return result.Task;
            });
        using var page = Page(client.Object, out var auth);
        page.GetContent();
        await page.CurrentWork;
        Submit(page, ActionData(page, "confirm"));
        var token = await started.Task;
        auth.SignOut();
        Assert.IsTrue(token.IsCancellationRequested);
        result.SetResult(new PullRequestMergeResult("merged", null, "Merged"));
        await page.CurrentWork;
        Assert.Contains("account changed", Template(page));
        Assert.DoesNotContain("Merged", Template(page));
    }

    [TestMethod]
    public async Task InvalidMethodOrUnconfirmedAction_NeverMutates()
    {
        var client = Client();
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"action":"confirm"}""");
        Submit(page, ActionData(page, "confirm"), """{"method":"merge"}""");
        Submit(page, ActionData(page, "confirm"), """{"method":"squash","scopeAccepted":"false"}""");
        Submit(page, ActionData(page, "confirm"), """{"method":"squash"}""");
        Submit(page, "[]");
        Submit(page, "{");
        VerifyNoMerge(client);
    }

    [TestMethod]
    public async Task FreshConfirmationAfterCancel_RotatesIdentityAndAllowsExplicitNewAttempt()
    {
        var client = Client();
        client.Setup(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestMergeResult("enqueued", null, "Enqueued, not merged"));
        using var page = Page(client.Object, out _);
        page.GetContent();
        await page.CurrentWork;
        var staleConfirmation = ActionData(page, "confirm");
        Submit(page, """{"action":"cancel"}""");
        Submit(page, ActionData(page, "prepare"));
        await page.CurrentWork;
        Submit(page, staleConfirmation);
        VerifyNoMerge(client);
        Submit(page, ActionData(page, "confirm"));
        await page.CurrentWork;
        Assert.Contains("Enqueued", Template(page));
        client.Verify(c => c.GetTargetAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()), Times.Exactly(2));
        client.Verify(c => c.MergeAsync(Account, Target, "squash", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CancellationDuringLoad_DiscardsTargetAndDoesNotOfferConfirmation()
    {
        var client = Client();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetTargetAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, int _, CancellationToken token) =>
            {
                started.SetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Target;
            });
        using var page = Page(client.Object, out _);
        page.GetContent();
        var token = await started.Task;
        Submit(page, """{"action":"cancel"}""");
        await page.CurrentWork;
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.DoesNotContain("Confirm merge", Template(page));
        VerifyNoMerge(client);
    }

    private static Mock<IPullRequestMergeClient> Client()
    {
        var client = new Mock<IPullRequestMergeClient>();
        client.Setup(c => c.GetTargetAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>())).ReturnsAsync(Target);
        return client;
    }

    private static MergePullRequestPage Page(IPullRequestMergeClient client, out AuthService auth)
    {
        var authClient = new Mock<IGitHubAuthClient>();
        authClient.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account.Login);
        auth = new AuthService(new InMemoryAccountStore(Account), authClient.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        return new(auth, client, Account, "octo/tool", 42, new Uri("https://github.com/octo/tool/pull/42"));
    }

    private static void Submit(MergePullRequestPage page, string data, string inputs = """{"method":"squash","scopeAccepted":"true"}""") =>
        ((IFormContent)page.GetContent()[0]).SubmitForm(inputs, data);

    private static string Template(MergePullRequestPage page) => ((IFormContent)page.GetContent()[0]).TemplateJson;

    private static string ActionData(MergePullRequestPage page, string action)
    {
        using var json = JsonDocument.Parse(Template(page));
        return json.RootElement.GetProperty("actions").EnumerateArray()
            .Single(a => a.TryGetProperty("data", out var data) && data.GetProperty("action").GetString() == action)
            .GetProperty("data").GetRawText();
    }

    private static void VerifyNoMerge(Mock<IPullRequestMergeClient> client) =>
        client.Verify(c => c.MergeAsync(It.IsAny<GitHubAccount>(), It.IsAny<PullRequestMergeTarget>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
}
