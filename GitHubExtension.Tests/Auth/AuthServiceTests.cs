// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Web;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class AuthServiceTests
{
    private static readonly OAuthOptions Configured = new("client-id", "client-secret");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Constructor_LoadsSavedAccount()
    {
        var saved = new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "token");
        var auth = new AuthService(new InMemoryAccountStore(saved), Mock.Of<IGitHubAuthClient>(), FakeBrowser.Approving(), Configured);

        Assert.IsTrue(auth.IsSignedIn);
        Assert.AreEqual(saved, auth.CurrentAccount);
    }

    [TestMethod]
    public void BuildAuthorizeUri_IncludesPkceAndScopes()
    {
        var uri = AuthService.BuildAuthorizeUri(GitHubHost.GitHubDotCom, "id", new Uri("http://127.0.0.1:1234/callback"), "st", "ch");
        var query = HttpUtility.ParseQueryString(uri.Query);

        Assert.AreEqual("https://github.com/login/oauth/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.AreEqual("id", query["client_id"]);
        Assert.AreEqual("http://127.0.0.1:1234/callback", query["redirect_uri"]);
        Assert.AreEqual(OAuthOptions.Scopes, query["scope"]);
        Assert.AreEqual("st", query["state"]);
        Assert.AreEqual("ch", query["code_challenge"]);
        Assert.AreEqual("S256", query["code_challenge_method"]);
    }

    [TestMethod]
    public async Task SignInWithGitHubAsync_HappyPath_SavesAccountAndRaisesEvent()
    {
        var store = new InMemoryAccountStore();
        string? verifierSent = null;
        string? challengeSent = null;
        var client = new Mock<IGitHubAuthClient>();
        client
            .Setup(c => c.ExchangeCodeAsync(GitHubHost.GitHubDotCom, Configured, "the-code", It.IsAny<Uri>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<GitHubHost, OAuthOptions, string, Uri, string, CancellationToken>((_, _, _, _, verifier, _) => verifierSent = verifier)
            .ReturnsAsync("gho_token");
        client.Setup(c => c.GetLoginAsync(GitHubHost.GitHubDotCom, "gho_token", It.IsAny<CancellationToken>())).ReturnsAsync("octocat");

        var browser = FakeBrowser.Approving();
        var auth = new AuthService(store, client.Object, browser, Configured);
        var raised = 0;
        auth.AccountChanged += (_, _) => raised++;

        var account = await auth.SignInWithGitHubAsync(TestContext.CancellationToken);
        challengeSent = HttpUtility.ParseQueryString(browser.LastOpened!.Query)["code_challenge"];

        Assert.AreEqual(new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "gho_token"), account);
        Assert.AreEqual(account, store.Account);
        Assert.AreEqual(account, auth.CurrentAccount);
        Assert.AreEqual(1, raised);
        Assert.AreEqual(challengeSent, Pkce.CreateChallenge(verifierSent!));
    }

    [TestMethod]
    public async Task SignInWithGitHubAsync_StateMismatch_Throws()
    {
        var browser = new FakeBrowser(uri =>
        {
            var redirect = HttpUtility.ParseQueryString(uri.Query)["redirect_uri"];
            return new Uri($"{redirect}?code=c&state=not-the-state");
        });
        var auth = new AuthService(new InMemoryAccountStore(), Mock.Of<IGitHubAuthClient>(), browser, Configured);

        await Assert.ThrowsAsync<GitHubAuthException>(() => auth.SignInWithGitHubAsync(TestContext.CancellationToken));
        Assert.IsFalse(auth.IsSignedIn);
    }

    [TestMethod]
    public async Task SignInWithGitHubAsync_UserDenied_ThrowsWithDescription()
    {
        var browser = new FakeBrowser(uri =>
        {
            var redirect = HttpUtility.ParseQueryString(uri.Query)["redirect_uri"];
            return new Uri($"{redirect}?error=access_denied&error_description=Nope");
        });
        var auth = new AuthService(new InMemoryAccountStore(), Mock.Of<IGitHubAuthClient>(), browser, Configured);

        var ex = await Assert.ThrowsAsync<GitHubAuthException>(() => auth.SignInWithGitHubAsync(TestContext.CancellationToken));
        Assert.AreEqual("Nope", ex.Message);
    }

    [TestMethod]
    public async Task SignInWithGitHubAsync_NotConfigured_Throws()
    {
        var browser = FakeBrowser.Approving();
        var auth = new AuthService(new InMemoryAccountStore(), Mock.Of<IGitHubAuthClient>(), browser, new OAuthOptions(null, null));

        Assert.IsFalse(auth.IsOAuthConfigured);
        await Assert.ThrowsAsync<GitHubAuthException>(() => auth.SignInWithGitHubAsync(TestContext.CancellationToken));
        Assert.IsNull(browser.LastOpened);
    }

    [TestMethod]
    public async Task SignInWithTokenAsync_ValidatesAndSaves()
    {
        var store = new InMemoryAccountStore();
        var client = new Mock<IGitHubAuthClient>();
        client.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), "ghp_token", It.IsAny<CancellationToken>())).ReturnsAsync("mona");
        var auth = new AuthService(store, client.Object, FakeBrowser.Approving(), Configured);

        var account = await auth.SignInWithTokenAsync("github.example.com", "  ghp_token  ", TestContext.CancellationToken);

        Assert.AreEqual("mona", account.Login);
        Assert.AreEqual(new Uri("https://github.example.com/"), account.Host.WebUrl);
        Assert.AreEqual("ghp_token", account.Token);
        Assert.AreEqual(account, store.Account);
    }

    [TestMethod]
    [DataRow(null, "token")]
    [DataRow("http://github.example.com", "token")]
    [DataRow("github.example.com", "")]
    [DataRow("github.example.com", "   ")]
    public async Task SignInWithTokenAsync_BadInput_Throws(string? server, string? token)
    {
        var client = new Mock<IGitHubAuthClient>(MockBehavior.Strict);
        var auth = new AuthService(new InMemoryAccountStore(), client.Object, FakeBrowser.Approving(), Configured);

        await Assert.ThrowsAsync<GitHubAuthException>(() => auth.SignInWithTokenAsync(server, token, TestContext.CancellationToken));
    }

    [TestMethod]
    public void SignOut_ClearsStoreAndRaisesEventOnce()
    {
        var store = new InMemoryAccountStore(new GitHubAccount(GitHubHost.GitHubDotCom, "octocat", "t"));
        var auth = new AuthService(store, Mock.Of<IGitHubAuthClient>(), FakeBrowser.Approving(), Configured);
        var raised = 0;
        auth.AccountChanged += (_, _) => raised++;

        auth.SignOut();
        auth.SignOut();

        Assert.IsNull(store.Account);
        Assert.IsFalse(auth.IsSignedIn);
        Assert.AreEqual(1, raised);
    }

    [TestMethod]
    public void OAuthOptions_IsConfigured_RequiresBothValues()
    {
        Assert.IsTrue(Configured.IsConfigured);
        Assert.IsFalse(new OAuthOptions("id", null).IsConfigured);
        Assert.IsFalse(new OAuthOptions(" ", "secret").IsConfigured);
    }
}
