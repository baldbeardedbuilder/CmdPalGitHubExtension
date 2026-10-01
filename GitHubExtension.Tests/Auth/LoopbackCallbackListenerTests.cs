// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class LoopbackCallbackListenerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ParseCallback_ReadsCodeAndState()
    {
        var callback = LoopbackCallbackListener.ParseCallback("/callback?code=abc123&state=x%2By");
        Assert.AreEqual("abc123", callback.Code);
        Assert.AreEqual("x+y", callback.State);
        Assert.IsNull(callback.Error);
    }

    [TestMethod]
    public void ParseCallback_ReadsErrors()
    {
        var callback = LoopbackCallbackListener.ParseCallback("/callback?error=access_denied&error_description=The+user+said+no&state=s");
        Assert.AreEqual("access_denied", callback.Error);
        Assert.AreEqual("The user said no", callback.ErrorDescription);
        Assert.IsNull(callback.Code);
    }

    [TestMethod]
    public void ParseCallback_NoQuery_ReturnsEmptyCallback()
    {
        Assert.AreEqual(new OAuthCallback(null, null, null, null), LoopbackCallbackListener.ParseCallback("/callback"));
    }

    [TestMethod]
    public void RedirectUri_UsesLoopbackIpAndCallbackPath()
    {
        using var listener = new LoopbackCallbackListener();
        Assert.AreEqual("127.0.0.1", listener.RedirectUri.Host);
        Assert.AreEqual(listener.Port, listener.RedirectUri.Port);
        Assert.AreEqual("/callback", listener.RedirectUri.AbsolutePath);
    }

    [TestMethod]
    public async Task WaitForCallbackAsync_IgnoresOtherPathsAndReturnsCallback()
    {
        using var listener = new LoopbackCallbackListener();
        using var http = new HttpClient();
        var waiting = listener.WaitForCallbackAsync(TestContext.CancellationToken);

        var favicon = await http.GetAsync(new Uri($"http://127.0.0.1:{listener.Port}/favicon.ico"), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.IsFalse(waiting.IsCompleted);

        var page = await http.GetStringAsync(new Uri(listener.RedirectUri, "?code=c0de&state=st4te"), TestContext.CancellationToken);
        var callback = await waiting;

        Assert.AreEqual("c0de", callback.Code);
        Assert.AreEqual("st4te", callback.State);
        Assert.Contains("You're signed in", page);
    }

    [TestMethod]
    public async Task WaitForCallbackAsync_Cancelled_Throws()
    {
        using var listener = new LoopbackCallbackListener();
        using var cts = new CancellationTokenSource();
        var waiting = listener.WaitForCallbackAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
    }
}
