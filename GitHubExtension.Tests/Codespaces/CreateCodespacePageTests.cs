// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public class CreateCodespacePageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");

    [TestMethod]
    public void CreatePage_ProvidesNavigationCommandAndForm()
    {
        using var page = CreatePage(Mock.Of<ICodespacesClient>(), out _);

        Assert.AreEqual(CreateCodespacePage.PageId, page.Id);
        Assert.AreEqual("Create Codespace", page.Name);
        Assert.Contains("repository", CurrentTemplate(page));
        Assert.Contains("branch", CurrentTemplate(page));
    }

    [TestMethod]
    public async Task Submit_CreatesCodespaceAndOffersToOpenIt()
    {
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(
                Account,
                "octocat/hello",
                "main",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace());
        using var page = CreatePage(client.Object, out var browser);

        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello","branch":"main"}""");
        Assert.Contains("compute time", CurrentTemplate(page));
        Assert.Contains("may incur charges", CurrentTemplate(page));
        Assert.Contains("octocat@github.com", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Confirm(page);
        await page.CurrentCreate;

        Assert.Contains("Codespace created", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", "main", It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(CommandResultKind.Dismiss, Submit(page, CreateCodespaceActions.Open).Kind);
        Assert.AreEqual(Codespace().WebUrl, browser.LastOpened);
    }

    [TestMethod]
    public void Submit_InvalidRepositoryShowsErrorWithoutCallingGitHub()
    {
        var client = new Mock<ICodespacesClient>();
        using var page = CreatePage(client.Object, out _);

        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat","branch":"main"}""");

        Assert.Contains("Enter a repository as owner/name.", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(
            It.IsAny<GitHubAccount>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Submit_ApiFailureReturnsToFormAndShowsError()
    {
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(
                Account,
                "octocat/hello",
                null,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Your token is missing the codespace scope."));
        using var page = CreatePage(client.Object, out _);

        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello","branch":""}""");
        Confirm(page);
        await page.CurrentCreate;

        Assert.Contains("Your token is missing the codespace scope.", CurrentTemplate(page));
        Assert.Contains("octocat/hello", CurrentTemplate(page));
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task Submit_AmbiguousOutcomeReconcilesBeforeReportingCreatedCodespace()
    {
        var client = new Mock<ICodespacesClient>();
        var codespace = Codespace();
        var error = new HttpRequestException("network unavailable");
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceCreate, DiagnosticArea.Codespaces);
                operation.Fail(error, DiagnosticFailure.Transport, outcome: DiagnosticOutcome.Unknown);
                return Task.FromException<GitHubCodespace>(error);
            });
        using var page = CreatePage(client.Object, out _);
        client.SetupSequence(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null))
            .ReturnsAsync(new CodespacesPageResult([codespace], null));

        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello","branch":"main"}""");
        Confirm(page);
        await page.CurrentCreate;

        Assert.Contains("Codespace created", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", "main", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task Submit_AmbiguousOutcomeWithoutMatchingCodespaceCannotBeResubmitted()
    {
        var client = new Mock<ICodespacesClient>();
        var error = new HttpRequestException("network unavailable");
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", "main", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceCreate, DiagnosticArea.Codespaces);
                operation.Fail(error, DiagnosticFailure.Transport, outcome: DiagnosticOutcome.Unknown);
                return Task.FromException<GitHubCodespace>(error);
            });
        using var page = CreatePage(client.Object, out _);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null));

        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello","branch":"main"}""");
        Confirm(page);
        await page.CurrentCreate;
        Assert.Contains("may have accepted", CurrentTemplate(page));
        Submit(page, CreateCodespaceActions.Check);
        await page.CurrentCreate;
        Submit(page, CreateCodespaceActions.Confirm, confirmation: "stale-confirmation");

        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", "main", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("may have accepted", CurrentTemplate(page));
    }

    private static GitHubCodespace Codespace() =>
        new("hello-abc", "Hello", "octocat/hello", "main", "Queued", DateTimeOffset.UtcNow, new Uri("https://hello-abc.github.dev"));

    private static CreateCodespacePage CreatePage(ICodespacesClient client, out FakeBrowser browser)
    {
        var authClient = new Mock<IGitHubAuthClient>();
        var auth = new AuthService(
            new InMemoryAccountStore(Account),
            authClient.Object,
            new FakeBrowser(_ => null),
            new OAuthOptions("id", "secret"));
        Mock.Get(client).Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null));
        browser = new FakeBrowser(_ => null);
        return new CreateCodespacePage(auth, client, browser);
    }

    private static ICommandResult Submit(CreateCodespacePage page, string action, string inputs = "{}", string? confirmation = null) =>
        ((IFormContent)page.GetContent()[0]).SubmitForm(inputs, confirmation is null
            ? $$"""{"action":"{{action}}"}"""
            : $$"""{"action":"{{action}}","confirmation":"{{confirmation}}"}""");

    private static void Confirm(CreateCodespacePage page)
    {
        using var json = JsonDocument.Parse(CurrentTemplate(page));
        var confirmation = json.RootElement.GetProperty("body")[2].GetProperty("actions")[0]
            .GetProperty("data").GetProperty("confirmation").GetString();
        Assert.IsNotNull(confirmation);
        Submit(page, CreateCodespaceActions.Confirm, confirmation: confirmation);
    }

    private static string CurrentTemplate(CreateCodespacePage page)
    {
        var template = ((IFormContent)page.GetContent()[0]).TemplateJson;
        using var json = JsonDocument.Parse(template);
        Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        return template;
    }
}
