// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Api;

[TestClass]
public sealed class GitHubRestTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "secret-token");
    private static readonly Uri Endpoint = new("https://api.github.com/repos/o/r/pulls/7?access_token=secret-query");

    [TestMethod]
    [DataRow(HttpStatusCode.Unauthorized, "GitHub didn't accept your token. Sign out and back in to fix it.")]
    [DataRow(HttpStatusCode.Forbidden, "GitHub said no. Your token might be missing a scope, or you hit a rate limit.")]
    [DataRow(HttpStatusCode.TooManyRequests, "GitHub said no. Your token might be missing a scope, or you hit a rate limit.")]
    [DataRow(HttpStatusCode.NotFound, "github.com returned 404 Not Found.")]
    public async Task SendAsync_HttpFailureLogsSafeDiagnosticsAndThrows(HttpStatusCode status, string expectedError)
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => ErrorResponse(status)));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: logs.Add));

        Assert.AreEqual(expectedError, error.Message);
        Assert.AreEqual(
            $"GitHub API error: GET https://api.github.com/repos/o/r/pulls/7; status={(int)status}; "
            + "request-id=test-request; rate-limit-remaining=0; rate-limit-reset=1790975000; sso-header-present=True.",
            Assert.ContainsSingle(logs));
    }

    [TestMethod]
    public async Task SendAsync_NonThrowingFailureStillLogs()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        using var response = await GitHubRest.SendAsync(
            http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, throwOnError: false, logError: logs.Add);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.AreEqual(
            "GitHub API error: GET https://api.github.com/repos/o/r/pulls/7; status=404; "
            + "request-id=unknown; rate-limit-remaining=unknown; rate-limit-reset=unknown; sso-header-present=False.",
            Assert.ContainsSingle(logs));
    }

    [TestMethod]
    public async Task SendAsync_SuccessDoesNotLog()
    {
        var logs = new List<string>();
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await GitHubRest.SendAsync(
            http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: logs.Add);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public async Task SendAsync_TransportFailureLogsCategoryWithoutExceptionSecrets()
    {
        var logs = new List<string>();
        var failure = new HttpRequestException(HttpRequestError.NameResolutionError, "secret-exception");
        using var http = new HttpClient(new StubHandler(_ => throw failure));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: logs.Add));

        Assert.AreSame(failure, error.InnerException);
        Assert.AreEqual(
            "GitHub API error: GET https://api.github.com/repos/o/r/pulls/7; transport=NameResolutionError.",
            Assert.ContainsSingle(logs));
    }

    [TestMethod]
    public async Task ReadJsonAsync_InvalidJsonLogsMetadataWithoutBody()
    {
        var logs = new List<string>();
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        using var response = ErrorResponse(HttpStatusCode.OK);
        response.RequestMessage = request;

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.ReadJsonAsync(response, TestContext.CancellationToken, logs.Add));

        Assert.AreEqual("GitHub sent back something we couldn't read.", error.Message);
        Assert.AreEqual(
            "GitHub API error: invalid JSON; endpoint=https://api.github.com/repos/o/r/pulls/7; status=200; request-id=test-request.",
            Assert.ContainsSingle(logs));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SendAsync_TimeoutLogsAndThrowsRetryableError(bool throwOnError)
    {
        var logs = new List<string>();
        var failure = new TaskCanceledException("secret-exception");
        using var http = new HttpClient(new StubHandler(_ => throw failure));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, throwOnError: throwOnError, logError: logs.Add));

        Assert.AreSame(failure, error.InnerException);
        Assert.AreEqual("The request to api.github.com timed out. Try again.", error.Message);
        Assert.AreEqual(
            "GitHub API error: GET https://api.github.com/repos/o/r/pulls/7; transport=Timeout.",
            Assert.ContainsSingle(logs));
    }

    [TestMethod]
    public async Task SendAsync_CallerCancellationDoesNotLogAnError()
    {
        var logs = new List<string>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var http = new HttpClient(new StubHandler(_ =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, cancellation.Token, logError: logs.Add));

        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public void LogEndpoint_RemovesCredentialsQueryAndFragment()
    {
        var uri = new Uri("https://user:secret-password@api.github.com/repos/o/r/pulls/7?token=secret-query#secret-fragment");

        Assert.AreEqual("https://api.github.com/repos/o/r/pulls/7", GitHubRest.LogEndpoint(uri));
        Assert.AreEqual("unknown", GitHubRest.LogEndpoint(null));
    }

    [TestMethod]
    public async Task GetSubjectAsync_HttpFailurePreservesActionableError()
    {
        using var http = new HttpClient(new StubHandler(_ => ErrorResponse(HttpStatusCode.Unauthorized)));
        var client = new NotificationsClient(http);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetSubjectAsync(Account, Endpoint, TestContext.CancellationToken));

        Assert.AreEqual("GitHub didn't accept your token. Sign out and back in to fix it.", error.Message);
    }

    [TestMethod]
    public async Task GetSubjectAsync_TimeoutThrowsActionableError()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new TaskCanceledException("secret-exception")));
        var client = new NotificationsClient(http);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetSubjectAsync(Account, Endpoint, TestContext.CancellationToken));

        Assert.AreEqual("The request to api.github.com timed out. Try again.", error.Message);
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent("secret-body") };
        response.Headers.Add("X-GitHub-Request-Id", "test-request");
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", "1790975000");
        response.Headers.Add("X-GitHub-SSO", "partial-results; organizations=21955855");
        return response;
    }

    [TestMethod]
    public async Task SendAsync_SsoRequiredThrowsAuthorizeUrl()
    {
        var authorize = "https://github.com/orgs/microsoft/sso?authorization_request=secret";
        using var http = new HttpClient(new StubHandler(_ => SsoResponse($"required; url={authorize}")));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: _ => { }));

        Assert.AreEqual("The microsoft organization requires SAML single sign-on. Authorize this app for microsoft, then refresh.", error.Message);
        Assert.AreEqual(new Uri(authorize), error.AuthorizeUrl);
    }

    [TestMethod]
    [DataRow("required")]
    [DataRow("required; url=https://evil.example.com/orgs/microsoft/sso")]
    [DataRow("required; url=http://github.com/orgs/microsoft/sso")]
    public async Task SendAsync_SsoRequiredWithoutTrustedUrlHasNoLink(string header)
    {
        using var http = new HttpClient(new StubHandler(_ => SsoResponse(header)));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            GitHubRest.SendAsync(http, Account, HttpMethod.Get, Endpoint, TestContext.CancellationToken, logError: _ => { }));

        Assert.AreEqual("An organization requires SAML single sign-on. Authorize this app for it on GitHub, then refresh.", error.Message);
        Assert.IsNull(error.AuthorizeUrl);
    }

    private static HttpResponseMessage SsoResponse(string header)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        response.Headers.Add("X-GitHub-SSO", header);
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    public TestContext TestContext { get; set; }
}
