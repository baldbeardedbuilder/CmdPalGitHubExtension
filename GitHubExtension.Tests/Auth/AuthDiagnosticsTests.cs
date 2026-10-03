// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Runtime.InteropServices;
using System.Web;
using BaldBeardedBuilder.CmdPal.GitHub.Api;

#pragma warning disable CA2201 // Fault injection simulates Windows Credential Locker failures.

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class AuthDiagnosticsTests
{
    private const string Sensitive = "private-secret-diagnostic-sentinel";
    private static readonly OAuthOptions Options = new(Sensitive, Sensitive);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BrowserSignIn_StagesShareCorrelationAndRedactSecrets()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var auth = new AuthService(new PasswordVaultAccountStore(new FakeCredentialVault()),
            CreateClient(), FakeBrowser.Approving(Sensitive), Options);
        entries.Clear();

        await auth.SignInWithGitHubAsync(TestContext.CancellationToken);

        DiagnosticEvent[] stages = [DiagnosticEvent.AuthSignIn, DiagnosticEvent.AuthBrowser,
            DiagnosticEvent.AuthCallback, DiagnosticEvent.AuthExchange, DiagnosticEvent.AuthIdentity,
            DiagnosticEvent.CredentialSave];
        foreach (var stage in stages)
        {
            Assert.AreEqual(1, entries.Count(entry => entry.Event == stage && entry.Outcome == DiagnosticOutcome.Requested));
            Assert.AreEqual(1, entries.Count(entry => entry.Event == stage && entry.Outcome == DiagnosticOutcome.Completed));
        }

        Assert.AreEqual(1, entries.Select(entry => entry.OperationId).Distinct().Count());
        Assert.IsTrue(entries.All(entry => entry.Area == DiagnosticArea.Auth));
        AssertRedacted(entries);
        var signInId = entries[0].OperationId;
        entries.Clear();

        auth.SignOut();

        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.CredentialClear && entry.Outcome == DiagnosticOutcome.Completed));
        Assert.AreEqual(1, entries.Select(entry => entry.OperationId).Distinct().Count());
        Assert.AreNotEqual(signInId, entries[0].OperationId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CallbackValidation_RecordsOneAuthenticationErrorAndPreservesUserError(bool denied)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var browser = new FakeBrowser(uri =>
        {
            var redirect = HttpUtility.ParseQueryString(uri.Query)["redirect_uri"];
            return new Uri(denied
                ? $"{redirect}?error=access_denied&error_description={Sensitive}"
                : $"{redirect}?code={Sensitive}&state={Sensitive}");
        });
        var auth = new AuthService(new InMemoryAccountStore(), CreateClient(), browser, Options);

        var error = await Assert.ThrowsAsync<GitHubAuthException>(() => auth.SignInWithGitHubAsync(TestContext.CancellationToken));

        if (denied)
        {
            Assert.AreEqual(Sensitive, error.Message);
        }

        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticEvent.AuthCallback, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Authentication, failure.Failure);
        Assert.AreEqual(1, entries.Select(entry => entry.OperationId).Distinct().Count());
        Assert.IsFalse(entries.Any(entry => entry.Event == DiagnosticEvent.AuthExchange));
        AssertRedacted(entries);
    }

    [TestMethod]
    public async Task BrowserLaunchFailure_IsRecordedBeforeCallbackWithoutLeakingUri()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var browser = new Mock<IBrowserLauncher>();
        var original = new InvalidOperationException(Sensitive);
        browser.Setup(value => value.Open(It.IsAny<Uri>())).Throws(original);
        var auth = new AuthService(new InMemoryAccountStore(), CreateClient(), browser.Object, Options);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.SignInWithGitHubAsync(TestContext.CancellationToken));

        Assert.AreSame(original, error);
        Assert.AreEqual(DiagnosticEvent.AuthBrowser, entries.Single(entry => entry.Severity == DiagnosticSeverity.Error).Event);
        Assert.IsFalse(entries.Any(entry => entry.Event == DiagnosticEvent.AuthCallback));
        AssertRedacted(entries);
    }

    [TestMethod]
    public async Task CallbackCancellation_IsNotAnError()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var cancellation = new CancellationTokenSource();
        var browser = new Mock<IBrowserLauncher>();
        browser.Setup(value => value.Open(It.IsAny<Uri>())).Callback(cancellation.Cancel);
        var auth = new AuthService(new InMemoryAccountStore(), CreateClient(), browser.Object, Options);

        await Assert.ThrowsAsync<OperationCanceledException>(() => auth.SignInWithGitHubAsync(cancellation.Token));

        Assert.IsFalse(entries.Any(entry => entry.Severity == DiagnosticSeverity.Error));
        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.AuthCallback && entry.Outcome == DiagnosticOutcome.Cancelled));
        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.AuthSignIn && entry.Outcome == DiagnosticOutcome.Cancelled));
    }

    [TestMethod]
    public async Task TokenCancellation_CancelsIdentityAndSignInWithoutError()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var auth = new AuthService(new InMemoryAccountStore(), CreateClient(), FakeBrowser.Approving(), Options);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            auth.SignInWithTokenAsync("github.com", Sensitive, cancellation.Token));

        Assert.IsFalse(entries.Any(entry => entry.Severity == DiagnosticSeverity.Error));
        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.AuthIdentity && entry.Outcome == DiagnosticOutcome.Cancelled));
        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.AuthTokenSignIn && entry.Outcome == DiagnosticOutcome.Cancelled));
    }

    [TestMethod]
    public async Task AuthTimeout_IsFailureRatherThanUserCancellation()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var timeout = new OperationCanceledException(Sensitive, new TimeoutException(Sensitive));

        await Assert.ThrowsAsync<GitHubAuthException>(() => AuthDiagnostics.RunAsync<string>(
            DiagnosticEvent.AuthCallback,
            () => Task.FromException<string>(new GitHubAuthException("We didn't hear back from your browser. Give it another try.", timeout)),
            CancellationToken.None));

        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticFailure.Timeout, failure.Failure);
        Assert.AreEqual(DiagnosticOutcome.Failed, failure.Outcome);
        AssertRedacted(entries);
    }

    [TestMethod]
    public async Task AuthFailure_PreservesPreviouslyRecordedCategoryWithoutInnerException()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var error = new GitHubAuthException(Sensitive);
        using var schema = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, DiagnosticArea.Auth);
        schema.Fail(error, DiagnosticFailure.Schema);

        await Assert.ThrowsAsync<GitHubAuthException>(() => AuthDiagnostics.RunAsync<string>(
            DiagnosticEvent.AuthIdentity, () => Task.FromException<string>(error), CancellationToken.None));

        var failure = entries.Single(entry => entry.Event == DiagnosticEvent.AuthIdentity && entry.Outcome == DiagnosticOutcome.Failed);
        Assert.AreEqual(DiagnosticFailure.Schema, failure.Failure);
        Assert.AreEqual(DiagnosticSeverity.Information, failure.Severity);
        AssertRedacted(entries);
    }

    [TestMethod]
    public async Task ExchangeRejected_PreservesDescriptionWithoutLoggingIt()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var client = CreateClient(exchangeBody: $"{{\"error\":\"bad_code\",\"error_description\":\"{Sensitive}\"}}");
        var auth = new AuthService(new InMemoryAccountStore(), client, FakeBrowser.Approving(), Options);

        var error = await Assert.ThrowsAsync<GitHubAuthException>(() => auth.SignInWithGitHubAsync(TestContext.CancellationToken));

        Assert.AreEqual(Sensitive, error.Message);
        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticEvent.AuthExchange, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Authentication, failure.Failure);
        AssertRedacted(entries);
    }

    [TestMethod]
    public async Task IdentityRejected_IsAuthenticationFailure()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var client = new GitHubAuthClient(new HttpClient(new AuthHandler("{}", Sensitive, HttpStatusCode.Unauthorized)));

        var error = await Assert.ThrowsAsync<GitHubAuthException>(() =>
            client.GetLoginAsync(GitHubHost.GitHubDotCom, Sensitive, TestContext.CancellationToken));

        Assert.AreEqual("That token was rejected. Double check it and try again.", error.Message);
        Assert.AreEqual(DiagnosticFailure.Authentication, entries.Single(entry => entry.Severity == DiagnosticSeverity.Error).Failure);
        AssertRedacted(entries);
    }

    [TestMethod]
    public async Task IdentityTransportFailure_DeduplicatesAndRedactsWrappedException()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var original = new HttpRequestException(Sensitive, new InvalidOperationException(Sensitive));
        var client = new GitHubAuthClient(new HttpClient(new AuthHandler("{}", "{}", failure: original)));
        var auth = new AuthService(new InMemoryAccountStore(), client, FakeBrowser.Approving(), Options);

        var error = await Assert.ThrowsAsync<GitHubAuthException>(() =>
            auth.SignInWithTokenAsync("github.com", Sensitive, TestContext.CancellationToken));

        Assert.AreSame(original, error.InnerException);
        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticEvent.AuthIdentity, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Transport, failure.Failure);
        AssertRedacted(entries);
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{\"login\":42}")]
    [DataRow("{}")]
    [DataRow("not json")]
    public async Task IdentityMalformedShape_IsSchemaFailureWithAuthUserError(string body)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var auth = new AuthService(new InMemoryAccountStore(), CreateClient(identityBody: body),
            FakeBrowser.Approving(), Options);

        await Assert.ThrowsAsync<GitHubAuthException>(() =>
            auth.SignInWithTokenAsync($"{Sensitive}.example.com", Sensitive, TestContext.CancellationToken));

        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(DiagnosticEvent.AuthIdentity, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Schema, failure.Failure);
        Assert.AreEqual(1, entries.Select(entry => entry.OperationId).Distinct().Count());
        AssertRedacted(entries);
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("{\"access_token\":42}")]
    [DataRow("{\"error_description\":{}}")]
    [DataRow("{}")]
    public async Task ExchangeMalformedShape_IsSchemaFailure(string body)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);

        await Assert.ThrowsAsync<GitHubAuthException>(() => CreateClient(exchangeBody: body).ExchangeCodeAsync(
            GitHubHost.GitHubDotCom, Options, Sensitive, new Uri("http://127.0.0.1:5/callback"), Sensitive,
            TestContext.CancellationToken));

        Assert.AreEqual(DiagnosticFailure.Schema, entries.Single(entry => entry.Severity == DiagnosticSeverity.Error).Failure);
        AssertRedacted(entries);
    }

    [TestMethod]
    [DataRow("load")]
    [DataRow("save")]
    [DataRow("clear")]
    public void CredentialFailure_IsCategorizedAndSafeWhenWrapped(string stage)
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var vault = new FakeCredentialVault(AccountStoreTests.Saved);
        var original = new COMException(Sensitive);
        var store = new PasswordVaultAccountStore(vault);
        var auth = new AuthService(store, CreateClient(), FakeBrowser.Approving(), Options);
        entries.Clear();
        Action action;
        switch (stage)
        {
            case "load":
                vault.FindFailure = _ => original;
                action = () => store.Load();
                break;
            case "save":
                vault.AddFailure = (_, _) => throw original;
                action = () => store.Save(AccountStoreTests.Replacement);
                break;
            default:
                vault.RemoveFailure = (_, _) => throw original;
                action = auth.SignOut;
                break;
        }

        var error = Assert.Throws<GitHubAuthException>(action);

        var failure = entries.Single(entry => entry.Severity == DiagnosticSeverity.Error);
        Assert.AreEqual(stage switch
        {
            "load" => DiagnosticEvent.CredentialLoad,
            "save" => DiagnosticEvent.CredentialSave,
            _ => DiagnosticEvent.CredentialClear,
        }, failure.Event);
        Assert.AreEqual(DiagnosticFailure.Credentials, failure.Failure);
        Assert.IsFalse(error.ToString().Contains(Sensitive, StringComparison.Ordinal));
        AssertRedacted(entries);
    }

    [TestMethod]
    public void CredentialMissingResource_CompletesNormally()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var store = new PasswordVaultAccountStore(new FakeCredentialVault());

        Assert.IsNull(store.Load());
        store.Clear();

        Assert.AreEqual(2, entries.Count(entry => entry.Outcome == DiagnosticOutcome.Completed));
        Assert.IsFalse(entries.Any(entry => entry.Severity == DiagnosticSeverity.Error));
    }

    [TestMethod]
    public async Task CredentialSaveFailure_DeduplicatesThroughTokenSignInAndPreservesCategory()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add);
        var vault = new FakeCredentialVault();
        var auth = new AuthService(new PasswordVaultAccountStore(vault), CreateClient(), FakeBrowser.Approving(), Options);
        entries.Clear();
        vault.AddFailure = (_, _) => throw new UnauthorizedAccessException(Sensitive);

        await Assert.ThrowsAsync<GitHubAuthException>(() =>
            auth.SignInWithTokenAsync("github.com", Sensitive, TestContext.CancellationToken));

        Assert.AreEqual(DiagnosticEvent.CredentialSave, entries.Single(entry => entry.Severity == DiagnosticSeverity.Error).Event);
        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.AuthTokenSignIn
            && entry.Outcome == DiagnosticOutcome.Failed && entry.Failure == DiagnosticFailure.Credentials
            && entry.Severity == DiagnosticSeverity.Information));
        Assert.AreEqual(1, entries.Select(entry => entry.OperationId).Distinct().Count());
        AssertRedacted(entries);
    }

    private static void AssertRedacted(IEnumerable<DiagnosticEntry> entries) =>
        Assert.IsFalse(string.Join('\n', entries).Contains(Sensitive, StringComparison.Ordinal));

    private static GitHubAuthClient CreateClient(string? exchangeBody = null, string? identityBody = null) =>
        new(new HttpClient(new AuthHandler(
            exchangeBody ?? $"{{\"access_token\":\"{Sensitive}\"}}",
            identityBody ?? $"{{\"login\":\"{Sensitive}\"}}")));

    private sealed class AuthHandler(string exchangeBody, string identityBody,
        HttpStatusCode status = HttpStatusCode.OK, Exception? failure = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
            {
                return Task.FromException<HttpResponseMessage>(failure);
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(request.Method == HttpMethod.Post ? exchangeBody : identityBody),
            });
        }
    }
}
