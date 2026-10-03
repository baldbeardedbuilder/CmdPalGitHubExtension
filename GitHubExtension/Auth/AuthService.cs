// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Api;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal sealed partial class AuthService : IDisposable
{
    public static readonly TimeSpan BrowserSignInTimeout = TimeSpan.FromMinutes(5);

    private readonly IAccountStore _store;
    private readonly IGitHubAuthClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly OAuthOptions _options;
    private readonly Func<LoopbackCallbackListener> _listenerFactory;
    private readonly Lock _lock = new();
    private GitHubAccount? _currentAccount;
    private readonly List<IAccountSubscription> _subscriptions = [];
    private HttpClient? _ownedHttp;

    internal const string OAuthNotConfiguredMessage = "This build doesn't have a GitHub OAuth app configured. See CONTRIBUTING.md to set one up.";

    public AuthService(
        IAccountStore store,
        IGitHubAuthClient client,
        IBrowserLauncher browser,
        OAuthOptions options,
        Func<LoopbackCallbackListener>? listenerFactory = null)
    {
        _store = store;
        _client = client;
        _browser = browser;
        _options = options;
        _listenerFactory = listenerFactory ?? (() => new LoopbackCallbackListener());
        _currentAccount = store.Load();
    }

    public event EventHandler? AccountChanged;

    public GitHubAccount? CurrentAccount
    {
        get
        {
            lock (_lock)
            {
                return _currentAccount;
            }
        }
    }

    public bool IsSignedIn => CurrentAccount is not null;

    public bool IsOAuthConfigured => _options.IsConfigured;

    public static AuthService CreateDefault() => CreateWithOwnedHttp(
        new PasswordVaultAccountStore(), new HttpClient(), new ShellBrowserLauncher(), OAuthOptions.FromAssembly());

    internal static AuthService CreateWithOwnedHttp(
        IAccountStore store, HttpClient http, IBrowserLauncher browser, OAuthOptions options) =>
        new(store, new GitHubAuthClient(http), browser, options) { _ownedHttp = http };

    internal IDisposable Subscribe<T>(T target, Action<T> changed) where T : class
    {
        var subscription = new AccountSubscription<T>(this, target, changed);
        lock (_lock)
        {
            _subscriptions.RemoveAll(entry => !entry.IsAlive);
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    internal void Unsubscribe(IAccountSubscription subscription)
    {
        lock (_lock)
        {
            _subscriptions.Remove(subscription);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _ownedHttp, null)?.Dispose();
    }

    internal static Uri BuildAuthorizeUri(GitHubHost host, string clientId, Uri redirectUri, string state, string codeChallenge)
    {
        var query = string.Join('&', new[]
        {
            ("client_id", clientId),
            ("redirect_uri", redirectUri.ToString()),
            ("scope", OAuthOptions.Scopes),
            ("state", state),
            ("code_challenge", codeChallenge),
            ("code_challenge_method", "S256"),
        }.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}"));

        return new Uri(host.WebUrl, $"login/oauth/authorize?{query}");
    }

    /// <summary>
    /// Opens the browser to GitHub, waits for the loopback redirect, and swaps the code for a token.
    /// </summary>
    public Task<GitHubAccount> SignInWithGitHubAsync(CancellationToken cancellationToken) =>
        AuthDiagnostics.RunAsync(DiagnosticEvent.AuthSignIn, () => SignInWithGitHubCoreAsync(cancellationToken), cancellationToken);

    private async Task<GitHubAccount> SignInWithGitHubCoreAsync(CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
        {
            throw new GitHubAuthException(OAuthNotConfiguredMessage);
        }

        var host = GitHubHost.GitHubDotCom;
        var state = Pkce.CreateRandomString();
        var verifier = Pkce.CreateRandomString();

        using var listener = _listenerFactory();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BrowserSignInTimeout);

        AuthDiagnostics.Run(DiagnosticEvent.AuthBrowser, () =>
            _browser.Open(BuildAuthorizeUri(host, _options.ClientId!, listener.RedirectUri, state, Pkce.CreateChallenge(verifier))));

        var callback = await AuthDiagnostics.RunAsync(DiagnosticEvent.AuthCallback, async () =>
        {
            OAuthCallback result;
            try
            {
                result = await listener.WaitForCallbackAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GitHubAuthException("We didn't hear back from your browser. Give it another try.", ex);
            }

            if (result.Error is not null)
            {
                throw new GitHubAuthException(result.ErrorDescription ?? result.Error);
            }

            if (result.State != state || string.IsNullOrEmpty(result.Code))
            {
                throw new GitHubAuthException("The sign in response didn't match what we sent. Give it another try.");
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);

        var token = await _client.ExchangeCodeAsync(host, _options, callback.Code!, listener.RedirectUri, verifier, cancellationToken).ConfigureAwait(false);
        return await CompleteSignInAsync(host, token, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in to GitHub Enterprise with a personal access token.
    /// </summary>
    public Task<GitHubAccount> SignInWithTokenAsync(string? serverUrl, string? token, CancellationToken cancellationToken) =>
        AuthDiagnostics.RunAsync(DiagnosticEvent.AuthTokenSignIn,
            () => SignInWithTokenCoreAsync(serverUrl, token, cancellationToken), cancellationToken);

    private async Task<GitHubAccount> SignInWithTokenCoreAsync(string? serverUrl, string? token, CancellationToken cancellationToken)
    {
        if (!GitHubHost.TryParse(serverUrl, out var host))
        {
            throw new GitHubAuthException("Enter your GitHub Enterprise URL, like https://github.example.com.");
        }

        var trimmedToken = token?.Trim();
        if (string.IsNullOrEmpty(trimmedToken))
        {
            throw new GitHubAuthException("Enter a personal access token.");
        }

        return await CompleteSignInAsync(host, trimmedToken, cancellationToken).ConfigureAwait(false);
    }

    public void SignOut()
    {
        AuthDiagnostics.Run(DiagnosticEvent.AuthSignOut, () => UpdateAccount(_store.Clear, null));
    }

    private async Task<GitHubAccount> CompleteSignInAsync(GitHubHost host, string token, CancellationToken cancellationToken)
    {
        var login = await _client.GetLoginAsync(host, token, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var account = new GitHubAccount(host, login, token);

        UpdateAccount(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _store.Save(account);
        }, account);
        return account;
    }

    private void UpdateAccount(Action persist, GitHubAccount? account)
    {
        var changed = false;
        try
        {
            lock (_lock)
            {
                var previous = _currentAccount;
                var persisted = false;
                try
                {
                    persist();
                    _currentAccount = account;
                    persisted = true;
                }
                catch
                {
                    try
                    {
                        _currentAccount = _store.Load();
                    }
                    catch
                    {
                        // The vault is unavailable. Preserve the last known account and the original error.
                    }

                    throw;
                }
                finally
                {
                    changed = previous != _currentAccount || (persisted && account is not null);
                }
            }
        }
        finally
        {
            if (changed)
            {
                AccountChanged?.Invoke(this, EventArgs.Empty);
                IAccountSubscription[] subscriptions;
                lock (_lock)
                {
                    _subscriptions.RemoveAll(entry => !entry.IsAlive);
                    subscriptions = [.. _subscriptions];
                }

                foreach (var subscription in subscriptions)
                {
                    subscription.Notify();
                }
            }
        }
    }
}
