// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class GitHubAuthClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ExchangeCodeAsync_PostsFormAndReturnsToken()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"access_token":"gho_abc","token_type":"bearer"}""");
        var client = new GitHubAuthClient(new HttpClient(handler));

        var token = await client.ExchangeCodeAsync(GitHubHost.GitHubDotCom, new OAuthOptions("id", "secret"), "code", new Uri("http://127.0.0.1:5/callback"), "verifier", TestContext.CancellationToken);

        Assert.AreEqual("gho_abc", token);
        Assert.AreEqual(HttpMethod.Post, handler.Request!.Method);
        Assert.AreEqual(new Uri("https://github.com/login/oauth/access_token"), handler.Request.RequestUri);
        Assert.Contains("code_verifier=verifier", handler.Body!);
        Assert.Contains("client_secret=secret", handler.Body!);
    }

    [TestMethod]
    public async Task ExchangeCodeAsync_ErrorResponse_ThrowsWithDescription()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"error":"bad_verification_code","error_description":"The code passed is incorrect or expired."}""");
        var client = new GitHubAuthClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<GitHubAuthException>(() =>
            client.ExchangeCodeAsync(GitHubHost.GitHubDotCom, new OAuthOptions("id", "secret"), "code", new Uri("http://127.0.0.1:5/callback"), "v", TestContext.CancellationToken));

        Assert.AreEqual("The code passed is incorrect or expired.", ex.Message);
    }

    [TestMethod]
    public async Task GetLoginAsync_UsesApiUrlAndBearerToken()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        var handler = new StubHandler(HttpStatusCode.OK, """{"login":"mona"}""");
        var client = new GitHubAuthClient(new HttpClient(handler));

        var login = await client.GetLoginAsync(host!, "ghp_x", TestContext.CancellationToken);

        Assert.AreEqual("mona", login);
        Assert.AreEqual(new Uri("https://github.example.com/api/v3/user"), handler.Request!.RequestUri);
        Assert.AreEqual("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.AreEqual("ghp_x", handler.Request.Headers.Authorization.Parameter);
        Assert.IsNotEmpty(handler.Request.Headers.UserAgent);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "{}")]
    [DataRow(HttpStatusCode.NotFound, "{}")]
    [DataRow(HttpStatusCode.OK, "<html>not json</html>")]
    public async Task GetLoginAsync_Failures_ThrowAuthException(HttpStatusCode status, string body)
    {
        var client = new GitHubAuthClient(new HttpClient(new StubHandler(status, body)));

        await Assert.ThrowsAsync<GitHubAuthException>(() => client.GetLoginAsync(GitHubHost.GitHubDotCom, "t", TestContext.CancellationToken));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
