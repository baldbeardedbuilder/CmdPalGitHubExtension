// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// What folks see before they've signed in. It's one adaptive card that swaps between the
/// github.com sign in, the GitHub Enterprise form, and the waiting and success states.
/// </summary>
internal sealed partial class SignInPage : ContentPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.signin";

    internal const string Message = "Sign in to access your notifications, repos, agents, codespaces, and saved queries.";

    private readonly AuthService _auth;
    private readonly Func<string> _logoProvider;
    private readonly Action<string> _showError;
    private readonly Lock _lock = new();
    private SignInForm _form;
    private CancellationTokenSource? _signInCancellation;
    private volatile bool _disposed;

    public SignInPage(AuthService auth, Func<string>? logoProvider = null, Action<string>? showError = null)
    {
        _auth = auth;
        _logoProvider = logoProvider ?? Icons.GetGitHubMarkDataUri;
        _showError = showError ?? ShowErrorToast;
        Id = PageId;
        Name = "Sign in";
        Title = "Sign in to GitHub";
        Icon = Icons.GitHub;
        _form = CreateForm(SignInView.Start);
    }

    internal SignInView CurrentView { get; private set; } = SignInView.Start;

    internal string? ErrorMessage { get; private set; }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            return [_form];
        }
    }

    internal ICommandResult HandleSubmit(string action, string inputs)
    {
        switch (action)
        {
            case SignInActions.GitHub:
                if (!_auth.IsOAuthConfigured)
                {
                    _showError(AuthService.OAuthNotConfiguredMessage);
                    break;
                }

                StartSignIn(ct => _auth.SignInWithGitHubAsync(ct), SignInView.WaitingForBrowser);
                break;

            case SignInActions.ShowEnterprise:
                Show(SignInView.Enterprise);
                break;

            case SignInActions.Enterprise:
                var (serverUrl, token) = ReadEnterpriseInputs(inputs);
                StartSignIn(ct => _auth.SignInWithTokenAsync(serverUrl, token, ct), SignInView.Verifying, serverUrl);
                break;

            case SignInActions.Cancel:
                CancelSignIn();
                Show(SignInView.Start);
                IsLoading = false;
                break;

            case SignInActions.Back:
                Show(SignInView.Start);
                break;

            case SignInActions.Done:
                return CommandResult.GoHome();
        }

        return CommandResult.KeepOpen();
    }

    private void StartSignIn(Func<CancellationToken, Task<GitHubAccount>> signIn, SignInView waitingView, string? serverUrl = null)
    {
        CancellationTokenSource? previous;
        var cancellation = new CancellationTokenSource();
        lock (_lock)
        {
            if (_disposed)
            {
                cancellation.Dispose();
                return;
            }

            previous = _signInCancellation;
            _signInCancellation = cancellation;
        }

        previous?.Cancel();
        var token = cancellation.Token;
        var returnView = waitingView == SignInView.Verifying ? SignInView.Enterprise : SignInView.Start;

        Show(waitingView);
        IsLoading = true;

        _ = Task.Run(async () =>
        {
            using var operation = OperationDiagnostics.Begin(DiagnosticEvent.Mutation, DiagnosticArea.Auth);
            Exception? failure = null;
            try
            {
                var account = await signIn(token).ConfigureAwait(false);
                if (!IsCurrentSignIn(cancellation))
                {
                    return;
                }

                Show(SignInView.SignedIn, account: account);
                new ToastStatusMessage($"Signed in as @{account.Login}").Show();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The user hit cancel, and we already moved them back.
            }
            catch (GitHubAuthException ex)
            {
                failure = ex;
                if (IsCurrentSignIn(cancellation))
                {
                    Show(returnView, ex.Message, serverUrl);
                }
            }
            catch (Exception ex)
            {
                failure = ex;
                if (IsCurrentSignIn(cancellation))
                {
                    Show(returnView, $"Something went wrong signing in. {ex.Message}", serverUrl);
                }
            }
            finally
            {
                bool current;
                lock (_lock)
                {
                    current = ReferenceEquals(_signInCancellation, cancellation);
                    if (current)
                    {
                        _signInCancellation = null;
                    }
                }

                cancellation.Dispose();
                if (current)
                {
                    if (!_disposed)
                    {
                        IsLoading = false;
                    }
                }

                PageDiagnostics.Finish(operation, failure, current, cancellationToken: token);
            }
        });
    }

    private void Show(SignInView view, string? error = null, string? serverUrl = null, GitHubAccount? account = null)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CurrentView = view;
            ErrorMessage = error;
            _form = CreateForm(view, error, serverUrl, account);
        }

        RaiseItemsChanged();
    }

    public void Dispose()
    {
        _disposed = true;
        CancelSignIn();
        IsLoading = false;
    }

    private void CancelSignIn()
    {
        CancellationTokenSource? cancellation;
        lock (_lock)
        {
            cancellation = _signInCancellation;
            _signInCancellation = null;
        }

        cancellation?.Cancel();
    }

    private bool IsCurrentSignIn(CancellationTokenSource cancellation)
    {
        lock (_lock)
        {
            return !_disposed && ReferenceEquals(_signInCancellation, cancellation);
        }
    }

    private SignInForm CreateForm(SignInView view, string? error = null, string? serverUrl = null, GitHubAccount? account = null)
    {
        var template = view switch
        {
            SignInView.Enterprise => SignInCards.Enterprise(error, serverUrl),
            SignInView.WaitingForBrowser => SignInCards.Waiting(_logoProvider(), "Finish signing in with your browser. We'll pick it up from there."),
            SignInView.Verifying => SignInCards.Waiting(_logoProvider(), "Checking your token..."),
            SignInView.SignedIn => SignInCards.SignedIn(_logoProvider(), account!),
            _ => SignInCards.Start(_logoProvider(), error),
        };

        return new SignInForm(this, template);
    }

    private static void ShowErrorToast(string message) =>
        new ToastStatusMessage(new StatusMessage { Message = message, State = MessageState.Error }).Show();

    private static (string? ServerUrl, string? Token) ReadEnterpriseInputs(string inputs)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrEmpty(inputs) ? "{}" : inputs);
            var root = json.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (Read("serverUrl"), Read("token"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private sealed partial class SignInForm : FormContent
    {
        private readonly SignInPage _page;

        public SignInForm(SignInPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.HandleSubmit(ReadAction(data), inputs);

        private static string ReadAction(string data)
        {
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrEmpty(data) ? "{}" : data);
                return json.RootElement.TryGetProperty("action", out var action) ? action.GetString() ?? string.Empty : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }
    }
}

internal enum SignInView
{
    Start,
    WaitingForBrowser,
    Enterprise,
    Verifying,
    SignedIn,
}

internal static class SignInActions
{
    public const string GitHub = "github";
    public const string ShowEnterprise = "showEnterprise";
    public const string Enterprise = "enterprise";
    public const string Cancel = "cancel";
    public const string Back = "back";
    public const string Done = "done";
}
