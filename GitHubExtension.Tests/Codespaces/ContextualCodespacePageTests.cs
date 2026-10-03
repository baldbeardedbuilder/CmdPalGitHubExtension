// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public sealed class ContextualCodespacePageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "token");

    [TestMethod]
    public async Task EnterpriseContext_NeverLoadsOrOffersCreation()
    {
        Assert.IsTrue(GitHubHost.TryParse("https://github.example.com", out var host));
        var account = new GitHubAccount(host, "octocat", "token");
        var auth = new AuthService(new InMemoryAccountStore(account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        var client = new Mock<ICodespacesClient>(MockBehavior.Strict);
        using var page = ContextualCodespacePage.ForRepository(
            auth, client.Object, new FakeBrowser(_ => null), "octocat/hello");

        page.GetContent();
        Submit(page, "create");
        Submit(page, "confirm-create");
        await page.CurrentOperation;

        using var card = JsonDocument.Parse(Template(page));
        Assert.Contains("isn't available on GitHub Enterprise Server",
            card.RootElement.GetProperty("body")[1].GetProperty("text").GetString()!);
        Assert.DoesNotContain("Confirm creation", Template(page));
        client.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task RepositoryContext_OpensAnExistingMatchingCodespace()
    {
        var codespace = Codespace();
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.GetRepositoryCodespacesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([codespace], null));
        var browser = new FakeBrowser(_ => null);
        using var page = ContextualCodespacePage.ForRepository(
            Auth(), client.Object, browser, "octocat/hello", "feature/demo");

        page.GetContent();
        await page.CurrentOperation;

        Assert.Contains("Found demo", Template(page));
        Assert.AreEqual(CommandResultKind.Dismiss, Submit(page, "open").Kind);
        Assert.AreEqual(codespace.WebUrl, browser.LastOpened);
        client.Verify(c => c.GetRepositoryCodespacesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task PullRequestContext_RequiresConfirmationAndUsesPullRequestCreateOperation()
    {
        var created = Codespace();
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.GetRepositoryCodespacesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null));
        client.Setup(c => c.CreatePullRequestCodespaceAsync(Account, "octocat/hello", 23, "feature/demo", It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        var browser = new FakeBrowser(_ => null);
        using var page = ContextualCodespacePage.ForPullRequest(
            Auth(), client.Object, browser, "octocat/hello", 23, "feature/demo");

        page.GetContent();
        await page.CurrentOperation;
        Submit(page, "create");
        Assert.Contains("may incur charges", Template(page));
        client.Verify(c => c.CreatePullRequestCodespaceAsync(
            It.IsAny<GitHubAccount>(), "octocat/hello", 23, "feature/demo", It.IsAny<CancellationToken>()), Times.Never);

        Submit(page, "confirm-create");
        await page.CurrentOperation;

        Assert.Contains("Codespace created", Template(page));
        client.Verify(c => c.CreatePullRequestCodespaceAsync(
            Account, "octocat/hello", 23, "feature/demo", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.CreateRepositoryCodespaceAsync(
            It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.AreEqual(CommandResultKind.Dismiss, Submit(page, "open").Kind);
        Assert.AreEqual(created.WebUrl, browser.LastOpened);
    }

    [TestMethod]
    public async Task RepositoryContext_AccountChangeDiscardsStaleLookup()
    {
        var response = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.GetRepositoryCodespacesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .Returns(response.Task);
        var auth = Auth();
        using var page = ContextualCodespacePage.ForRepository(
            auth, client.Object, new FakeBrowser(_ => null), "octocat/hello", "feature/demo");

        page.GetContent();
        auth.SignOut();
        response.SetResult(new CodespacesPageResult([Codespace()], null));
        await page.CurrentOperation;

        Assert.Contains("Checking for a Codespace", Template(page));
    }

    [TestMethod]
    public async Task Create_AccountChangeCancelsAndDiscardsStaleResponse()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.GetRepositoryCodespacesAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null));
        client.Setup(c => c.CreateRepositoryCodespaceAsync(
                Account, "octocat/hello", "feature/demo", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, string? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        var auth = Auth();
        using var page = ContextualCodespacePage.ForRepository(
            auth, client.Object, new FakeBrowser(_ => null), "octocat/hello", "feature/demo");

        page.GetContent();
        await page.CurrentOperation;
        Submit(page, "create");
        Submit(page, "confirm-create");
        var token = await started.Task;

        auth.SignOut();
        response.SetResult(Codespace());
        await page.CurrentOperation;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.Contains("Creating a Codespace", Template(page));
        Assert.DoesNotContain("Codespace created", Template(page));
        client.Verify(c => c.GetRepositoryCodespacesAsync(
            Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AuthService Auth() => new(
        new InMemoryAccountStore(Account),
        Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null),
        new OAuthOptions("id", "secret"));

    private static GitHubCodespace Codespace() => new(
        "demo", "Demo", "octocat/hello", "feature/demo", "Available",
        DateTimeOffset.UtcNow, new Uri("https://demo.github.dev"));

    private static ICommandResult Submit(ContentPage page, string action) =>
        ((IFormContent)page.GetContent().Single()).SubmitForm("{}", $$"""{"action":"{{action}}"}""");

    private static string Template(ContentPage page)
    {
        var template = ((IFormContent)page.GetContent().Single()).TemplateJson;
        using var json = JsonDocument.Parse(template);
        Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        return template;
    }
}
