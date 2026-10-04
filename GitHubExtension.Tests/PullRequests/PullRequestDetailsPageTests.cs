// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.PullRequests;

[TestClass]
public sealed class PullRequestDetailsPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FailedReviewSubmission_RetainsPendingReviewAndRetryDoesNotCreateAnother()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        client.SetupSequence(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details())
            .ReturnsAsync(Details());
        client.Setup(c => c.CreatePendingReviewAsync(Account, "octo/tool", 7, "head-7", "Original review", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PendingPullRequestReview(21, "head-7", "Original review"));
        client.SetupSequence(c => c.SubmitReviewAsync(Account, "octo/tool", 7,
                It.Is<PendingPullRequestReview>(review => review.Id == 21), "APPROVE", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Submission denied."))
            .ReturnsAsync(new PullRequestReview(21, Account.Login, "APPROVED", "Original review", "head-7", DateTimeOffset.UtcNow));
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7);

        page.GetContent();
        await page.CurrentWork;
        SubmitReview(page);
        await page.CurrentWork;

        var retryTemplate = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson;
        Assert.Contains("pending review draft is ready to submit again", retryTemplate);
        Assert.Contains("Original review", retryTemplate);

        SubmitReview(page);
        await page.CurrentWork;

        client.Verify(c => c.CreatePendingReviewAsync(Account, "octo/tool", 7, "head-7", "Original review",
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.SubmitReviewAsync(Account, "octo/tool", 7,
            It.Is<PendingPullRequestReview>(review => review.Id == 21), "APPROVE", It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Contains("Review submitted as approved.", Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson);
    }

    [TestMethod]
    public async Task ApprovingWithoutReviewBody_CreatesPinnedPendingReview()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        client.SetupSequence(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details())
            .ReturnsAsync(Details());
        client.Setup(c => c.CreatePendingReviewAsync(Account, "octo/tool", 7, "head-7", string.Empty, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PendingPullRequestReview(22, "head-7", string.Empty));
        client.Setup(c => c.SubmitReviewAsync(Account, "octo/tool", 7,
                It.Is<PendingPullRequestReview>(review => review.Id == 22 && review.Body == string.Empty),
                "APPROVE", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PullRequestReview(22, Account.Login, "APPROVED", string.Empty, "head-7", DateTimeOffset.UtcNow));
        var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7);

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"event":"APPROVE","reviewBody":""}""", """{"action":"prepare-review"}""");
        Confirm(page);
        await page.CurrentWork;

        client.Verify(c => c.CreatePendingReviewAsync(Account, "octo/tool", 7, "head-7", string.Empty,
            It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.SubmitReviewAsync(Account, "octo/tool", 7,
            It.Is<PendingPullRequestReview>(review => review.Id == 22), "APPROVE", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task AccountSwitchWhileCreatingPendingReview_CancelsAndDoesNotSubmitIt()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        client.Setup(c => c.GetDetailsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details());
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<PendingPullRequestReview>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.CreatePendingReviewAsync(Account, "octo/tool", 7, "head-7", "Original review", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, int _, string _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return finish.Task;
            });
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7);

        page.GetContent();
        await page.CurrentWork;
        SubmitReview(page);
        var token = await started.Task.WaitAsync(TestContext.CancellationToken);

        await auth.SignInWithTokenAsync("https://github.com", "replacement-token", TestContext.CancellationToken);
        Assert.IsTrue(token.IsCancellationRequested);
        finish.SetResult(new PendingPullRequestReview(23, "head-7", "Original review"));
        await page.CurrentWork;

        client.Verify(c => c.SubmitReviewAsync(Account, "octo/tool", 7, It.IsAny<PendingPullRequestReview>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        page.GetContent();
        await page.CurrentWork;
        Assert.DoesNotContain("Original review", Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson);
    }

    [TestMethod]
    public async Task StaleExternalGeneration_DoesNotLoad()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7, isCurrent: () => false);

        page.GetContent();
        await page.CurrentWork;

        client.Verify(c => c.GetDetailsAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsNull(page.ContextualCodespaceCommand);
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task ExternalGenerationChangeAfterPendingReview_DoesNotSubmitOrPublish()
    {
        var current = true;
        var client = new Mock<IPullRequestFeatureClient>();
        client.Setup(c => c.GetDetailsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details());
        client.Setup(c => c.CreatePendingReviewAsync(Account, "octo/tool", 7, "head-7", "Original review", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                current = false;
                return new PendingPullRequestReview(23, "head-7", "Original review");
            });
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7, isCurrent: () => current);

        page.GetContent();
        await page.CurrentWork;
        SubmitReview(page);
        await page.CurrentWork;

        client.Verify(c => c.SubmitReviewAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<PendingPullRequestReview>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.GetDetailsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ContextualCodespaceFactoryReceivesPullRequestHeadBranch()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        client.Setup(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details());
        var browser = new FakeBrowser(_ => null);
        var command = new OpenInBrowserCommand(browser, new Uri("https://github.com/octo/tool/codespaces"),
            "Open Codespace", Icons.Codespaces);
        string? factoryRepository = null;
        int factoryNumber = 0;
        string? factoryBranch = null;
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            browser, new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7,
            (repository, number, branch) =>
            {
                factoryRepository = repository;
                factoryNumber = number;
                factoryBranch = branch;
                return command;
            });

        page.GetContent();
        await page.CurrentWork;

        Assert.AreSame(command, page.ContextualCodespaceCommand);
        Assert.AreEqual("octo/tool", factoryRepository);
        Assert.AreEqual(7, factoryNumber);
        Assert.AreEqual("feature", factoryBranch);
    }

    [TestMethod]
    public async Task ContextualCodespaceCommand_IsDisposedWithPageAndRejectedWhenStale()
    {
        var current = true;
        var client = new Mock<IPullRequestFeatureClient>();
        client.Setup(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details());
        var created = new List<DisposableCommand>();
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7,
            (_, _, _) =>
            {
                var command = new DisposableCommand();
                created.Add(command);
                return command;
            },
            () => current);

        page.GetContent();
        await page.CurrentWork;
        var cached = page.ContextualCodespaceCommand;
        Assert.AreSame(cached, page.ContextualCodespaceCommand);
        Assert.HasCount(1, created);

        page.Dispose();
        Assert.IsTrue(created[0].Disposed);
        Assert.IsNull(page.ContextualCodespaceCommand);

        using var stale = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7,
            (_, _, _) =>
            {
                current = false;
                var command = new DisposableCommand();
                created.Add(command);
                return command;
            },
            () => current);
        stale.GetContent();
        await stale.CurrentWork;

        Assert.IsNull(stale.ContextualCodespaceCommand);
        Assert.HasCount(2, created);
        Assert.IsTrue(created[1].Disposed);
    }

    private sealed partial class DisposableCommand : InvokableCommand, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
    [TestMethod]
    public async Task CancelDraftConfirmationDoesNotCallClient()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        client.Setup(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Details());
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7);

        page.GetContent();
        await page.CurrentWork;
        Submit(page, "{}", """{"action":"draft"}""");
        var template = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson;
        using var card = JsonDocument.Parse(template);
        var cancelData = card.RootElement.GetProperty("body").EnumerateArray()
            .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "ActionSet")
            .SelectMany(element => element.GetProperty("actions").EnumerateArray())
            .Single(action => action.GetProperty("title").GetString() == "Cancel")
            .GetProperty("data").GetRawText();

        Submit(page, "{}", cancelData);

        client.Verify(c => c.SetDraftAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("Convert to draft", Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson);
    }

    private static void SubmitReview(PullRequestDetailsPage page)
    {
        Submit(page, """{"event":"APPROVE","reviewBody":"Original review"}""", """{"action":"prepare-review"}""");
        Confirm(page);
    }

    [TestMethod]
    public async Task NativeRefresh_ReloadsMarkdownWithoutBodyButtonAndRejectsStaleCommand()
    {
        const string markdown = "## Description\n\n- First\n\n```csharp\nvar value = 1;\n```";
        var details = Details() with { PullRequest = Details().PullRequest with { Body = markdown } };
        var client = new Mock<IPullRequestFeatureClient>();
        client.Setup(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7);
        page.GetContent();
        await page.CurrentWork;
        Assert.Contains(markdown, Assert.ContainsSingle(page.GetContent().OfType<MarkdownContent>()).Body);
        var form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        Assert.DoesNotContain("\"action\":\"refresh\"", form.TemplateJson);
        var refresh = Assert.ContainsSingle(page.Commands.OfType<CommandContextItem>());
        Assert.AreEqual("Refresh", refresh.Command!.Name);
        var command = Assert.IsInstanceOfType<IInvokableCommand>(refresh.Command);

        command.Invoke(null!);
        await page.CurrentWork;
        command.Invoke(null!);
        await page.CurrentWork;

        client.Verify(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task NativeRefresh_RecoversFailedInitialLoad()
    {
        var client = new Mock<IPullRequestFeatureClient>();
        client.SetupSequence(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Unavailable."))
            .ReturnsAsync(Details());
        using var auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        using var page = new PullRequestDetailsPage(auth, client.Object, Account, "octo/tool", 7);
        page.GetContent();
        await page.CurrentWork;
        Assert.Contains("Unavailable.", Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson);
        page.GetContent();
        client.Verify(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()), Times.Once);
        var refresh = Assert.ContainsSingle(page.Commands.OfType<CommandContextItem>());

        Assert.IsInstanceOfType<IInvokableCommand>(refresh.Command).Invoke(null!);
        await page.CurrentWork;

        Assert.Contains("Improve login", Assert.ContainsSingle(page.GetContent().OfType<MarkdownContent>()).Body);
        client.Verify(c => c.GetDetailsAsync(Account, "octo/tool", 7, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static void Confirm(PullRequestDetailsPage page)
    {
        var template = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson;
        using var card = JsonDocument.Parse(template);
        var data = card.RootElement.GetProperty("body").EnumerateArray()
            .Where(element => element.TryGetProperty("type", out var type) && type.GetString() == "ActionSet")
            .SelectMany(element => element.GetProperty("actions").EnumerateArray())
            .Single(action => action.GetProperty("title").GetString() == "Confirm review")
            .GetProperty("data").GetRawText();
        Submit(page, "{}", data);
    }

    private static void Submit(PullRequestDetailsPage page, string inputs, string data) =>
        Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).SubmitForm(inputs, data);

    private static PullRequestDetailsSnapshot Details() => new(
        new GitHubPullRequest
        {
            Number = 7,
            Title = "Improve login",
            WebUrl = new Uri("https://github.com/octo/tool/pull/7"),
            State = SubjectState.Open,
            Author = "contributor",
            HeadRef = "feature",
        },
        "head-7",
        "base-7",
        true,
        [],
        [],
        [],
        "success",
        PullRequestChecksState.Success,
        null,
        null,
        null,
        null,
        true);
}
