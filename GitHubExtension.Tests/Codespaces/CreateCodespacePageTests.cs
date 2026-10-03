// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
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
        Assert.Contains("may incur charges", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", "main", It.IsAny<CancellationToken>()), Times.Never);
        Submit(page, CreateCodespaceActions.Confirm);
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
        Submit(page, CreateCodespaceActions.Confirm);
        await page.CurrentCreate;

        Assert.Contains("Your token is missing the codespace scope.", CurrentTemplate(page));
        Assert.Contains("octocat/hello", CurrentTemplate(page));
        Assert.IsFalse(page.IsLoading);
    }

    private static GitHubCodespace Codespace() =>
        new("hello-abc", "Hello", "octocat/hello", "main", "Queued", DateTimeOffset.UtcNow, new Uri("https://hello-abc.github.dev"));

    [TestMethod]
    public async Task RepeatedConfirm_DoesNotCancelOrDuplicateCreation()
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, string? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello"}""");
        Submit(page, CreateCodespaceActions.Confirm);
        var token = await started.Task;
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/other"}""");
        Submit(page, CreateCodespaceActions.Confirm);
        Assert.IsFalse(token.IsCancellationRequested);
        response.SetResult(Codespace());
        await page.CurrentCreate;
        client.Verify(c => c.CreateCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccountChangeOrDispose_CancelsAndSuppressesCreation(bool dispose)
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, string? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello"}""");
        Submit(page, CreateCodespaceActions.Confirm);
        var task = page.CurrentCreate;
        var token = await started.Task;
        if (dispose)
        {
            page.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Codespace());
        await task;
        Assert.DoesNotContain("Codespace created", CurrentTemplate(page));
    }

    [TestMethod]
    public async Task AmbiguousCreate_CannotBeSubmittedAgain()
    {
        var entries = new System.Collections.Concurrent.ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Response lost.", outcomeUnknown: true));
        using var page = CreatePage(client.Object, out _);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello"}""");
            Submit(page, CreateCodespaceActions.Confirm);
            await page.CurrentCreate;
        }

        Assert.Contains("Creation is blocked", CurrentTemplate(page));
        Assert.IsTrue(entries.Any(e => e.Event == DiagnosticEvent.CodespaceCreate && e.Outcome == DiagnosticOutcome.Unknown));
        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
        Submit(page, "acknowledgeUnknownCreate");
        Assert.Contains("Creation is blocked", CurrentTemplate(page));
        Assert.DoesNotContain("Action.Submit", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace());
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello"}""");
        Submit(page, CreateCodespaceActions.Confirm);
        await page.CurrentCreate;
        Assert.Contains("Creation is blocked", CurrentTemplate(page));
        client.Verify(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StaleReviewForm_CannotConfirmAReplacementReview()
    {
        var client = new Mock<ICodespacesClient>();
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello"}""");
        var stale = (IFormContent)page.GetContent().Single();
        Submit(page, "cancel");
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/other"}""");
        stale.SubmitForm("{}", """{"action":"confirmCreate"}""");
        await page.CurrentCreate;
        client.Verify(c => c.CreateCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("octocat/other", CurrentTemplate(page));
    }

    [TestMethod]
    public async Task SsoFailure_OffersAuthorizationLink()
    {
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Authorize access.", authorizeUrl: new Uri("https://github.com/orgs/example/sso")));
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateCodespaceActions.Create, """{"repository":"octocat/hello"}""");
        Submit(page, CreateCodespaceActions.Confirm);
        await page.CurrentCreate;
        Assert.Contains("Action.OpenUrl", CurrentTemplate(page));
        Assert.Contains("https://github.com/orgs/example/sso", CurrentTemplate(page));
    }

    private static CreateCodespacePage CreatePage(ICodespacesClient client, out FakeBrowser browser)
        => CreatePage(client, out browser, out _);

    private static CreateCodespacePage CreatePage(ICodespacesClient client, out FakeBrowser browser, out AuthService auth)
    {
        var authClient = new Mock<IGitHubAuthClient>();
        auth = new AuthService(
            new InMemoryAccountStore(Account),
            authClient.Object,
            new FakeBrowser(_ => null),
            new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        return new CreateCodespacePage(auth, client, browser);
    }

    private static ICommandResult Submit(CreateCodespacePage page, string action, string inputs = "{}") =>
        ((IFormContent)page.GetContent()[0]).SubmitForm(inputs, $$"""{"action":"{{action}}"}""");

    private static string CurrentTemplate(CreateCodespacePage page)
    {
        var template = ((IFormContent)page.GetContent()[0]).TemplateJson;
        using var json = JsonDocument.Parse(template);
        Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        return template;
    }
}
