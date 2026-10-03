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
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private SignInForm _form;

    internal Task CurrentSignIn => _load.CurrentLoad;

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
        if (_load.Disposed)
        {
            return CommandResult.KeepOpen();
        }

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
                lock (_lock)
                {
                    _load.Invalidate(reset: true);
                }

                IsLoading = false;
                Show(SignInView.Start);
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
        ListLoadState.Operation request;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Invalidate(reset: true);
            _load.TryBegin(true, out request);
        }

        var returnView = waitingView == SignInView.Verifying ? SignInView.Enterprise : SignInView.Start;

        _load.Publish(request, () => Show(waitingView));
        _load.Publish(request, () => IsLoading = true);

        _load.Run(request, async () =>
        {
            var account = await signIn(request.Token).ConfigureAwait(false);
            _load.Publish(request, () => Show(SignInView.SignedIn, account: account));
            _load.Publish(request, () => new ToastStatusMessage($"Signed in as @{account.Login}").Show());
        }, () =>
        {
            string? error;
            lock (_lock)
            {
                error = _load.Error;
            }

            if (error is not null)
            {
                _load.Publish(request, () => Show(returnView, error, serverUrl));
            }

            _load.Publish(request, () => IsLoading = false);
        }, "GitHub took too long to respond. Try signing in again.", area: DiagnosticArea.Auth,
            diagnosticEvent: DiagnosticEvent.Mutation);
    }

    private void Show(SignInView view, string? error = null, string? serverUrl = null, GitHubAccount? account = null)
    {
        lock (_lock)
        {
            if (_load.Disposed)
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
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
        }

        IsLoading = false;
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
