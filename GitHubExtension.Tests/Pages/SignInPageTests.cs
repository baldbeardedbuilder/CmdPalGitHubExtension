// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public class SignInPageTests
{
    private const string Logo = "data:image/png;base64,AAAA";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void StartCard_HasMessageAndBothSignInOptions()
    {
        var page = CreatePage(out _);
        var template = CurrentTemplate(page);

        Assert.Contains(SignInPage.Message, template);
        Assert.Contains("Sign in with GitHub", template);
        Assert.Contains("Sign in with GitHub Enterprise account", template);
        Assert.Contains(Logo, template);
    }

    [TestMethod]
    public void StartCard_CentersPositiveGitHubActionAndKeepsEnterpriseLinkAction()
    {
        using var json = JsonDocument.Parse(SignInCards.Start(Logo, null));
        var body = json.RootElement.GetProperty("body");
        var actionLayout = body.EnumerateArray().Single(element => element.GetProperty("type").GetString() == "ColumnSet");
        var columns = actionLayout.GetProperty("columns");
        Assert.AreEqual(3, columns.GetArrayLength());

        var action = columns[1].GetProperty("items")[0].GetProperty("actions")[0];
        Assert.AreEqual("positive", action.GetProperty("style").GetString());
        Assert.AreEqual(Logo, action.GetProperty("iconUrl").GetString());

        var enterpriseLink = body.EnumerateArray().Single(element => element.GetProperty("type").GetString() == "Container");
        Assert.AreEqual(SignInActions.ShowEnterprise, enterpriseLink.GetProperty("selectAction").GetProperty("data").GetProperty("action").GetString());
        Assert.IsTrue(enterpriseLink.GetRawText().Contains("\"underline\": true", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AllCards_AreValidJson()
    {
        var account = new GitHubAccount(GitHubHost.GitHubDotCom, "octo\"cat", "t");
        string[] cards =
        [
            SignInCards.Start(Logo, "an \"error\" with\nnewlines"),
            SignInCards.Start(string.Empty, null),
            SignInCards.Waiting(Logo, "waiting"),
            SignInCards.SignedIn(Logo, account),
            SignInCards.Enterprise("bad", "https://github.example.com\"}"),
            SignInCards.Enterprise(null, null),
        ];

        foreach (var card in cards)
        {
            using var json = JsonDocument.Parse(card);
            Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        }
    }

    [TestMethod]
    public void ShowEnterprise_ThenBack_SwapsCards()
    {
        var page = CreatePage(out _);

        Submit(page, SignInActions.ShowEnterprise);
        Assert.AreEqual(SignInView.Enterprise, page.CurrentView);
        Assert.Contains("serverUrl", CurrentTemplate(page));

        Submit(page, SignInActions.Back);
        Assert.AreEqual(SignInView.Start, page.CurrentView);
    }

    [TestMethod]
    public async Task EnterpriseSubmit_BadToken_ShowsErrorAndKeepsServerUrl()
    {
        var page = CreatePage(out var client);
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), "bad", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubAuthException("That token was rejected."));

        Submit(page, SignInActions.ShowEnterprise);
        var result = Submit(page, SignInActions.Enterprise, """{"serverUrl":"github.example.com","token":"bad"}""");
        Assert.AreEqual(CommandResultKind.KeepOpen, result.Kind);

        await WaitForAsync(() => page.CurrentView == SignInView.Enterprise && page.ErrorMessage is not null);
        Assert.AreEqual("That token was rejected.", page.ErrorMessage);
        Assert.Contains("github.example.com", CurrentTemplate(page));
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task EnterpriseSubmit_GoodToken_ShowsSignedIn()
    {
        var page = CreatePage(out var client);
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), "good", It.IsAny<CancellationToken>())).ReturnsAsync("mona");

        Submit(page, SignInActions.Enterprise, """{"serverUrl":"github.example.com","token":"good"}""");

        await WaitForAsync(() => page.CurrentView == SignInView.SignedIn);
        Assert.Contains("@mona", CurrentTemplate(page));
        Assert.AreEqual(CommandResultKind.GoHome, Submit(page, SignInActions.Done).Kind);
    }

    [TestMethod]
    public void GitHubSubmit_WithoutOAuthConfig_ShowsToastAndStaysOnStart()
    {
        string? toast = null;
        var page = CreatePage(out _, new OAuthOptions(null, null), message => toast = message);

        Submit(page, SignInActions.GitHub);

        Assert.AreEqual(AuthService.OAuthNotConfiguredMessage, toast);
        Assert.AreEqual(SignInView.Start, page.CurrentView);
        Assert.IsNull(page.ErrorMessage);
        Assert.DoesNotContain("OAuth", CurrentTemplate(page));
    }

    private static SignInPage CreatePage(out Mock<IGitHubAuthClient> client, OAuthOptions? options = null, Action<string>? showError = null)
    {
        client = new Mock<IGitHubAuthClient>();
        var auth = new AuthService(new InMemoryAccountStore(), client.Object, new FakeBrowser(_ => null), options ?? new OAuthOptions("id", "secret"));
        return new SignInPage(auth, () => Logo, showError ?? (_ => { }));
    }

    private static ICommandResult Submit(SignInPage page, string action, string inputs = "{}") =>
        ((IFormContent)page.GetContent()[0]).SubmitForm(inputs, $$"""{"action":"{{action}}"}""");

    private static string CurrentTemplate(SignInPage page) => ((IFormContent)page.GetContent()[0]).TemplateJson;

    private async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
