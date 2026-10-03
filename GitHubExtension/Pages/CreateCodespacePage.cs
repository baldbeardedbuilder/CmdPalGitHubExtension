// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class CreateCodespacePage : ContentPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.create-codespace";

    private readonly AuthService _auth;
    private readonly ICodespacesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private CreateCodespaceForm _form;
    private GitHubCodespace? _createdCodespace;
    private bool _disposed;

    public CreateCodespacePage(AuthService auth, ICodespacesClient client, IBrowserLauncher browser)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        Id = PageId;
        Name = "Create Codespace";
        Title = "Create Codespace";
        Icon = Icons.Codespaces;
        _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
        _auth.AccountChanged += OnAccountChanged;
    }

    internal Task CurrentCreate
    {
        get
        {
            lock (_lock)
            {
                return _load.CurrentLoad;
            }
        }
    }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            return [_form];
        }
    }

    internal ICommandResult HandleSubmit(string inputs, string data)
    {
        if (_disposed)
        {
            return CommandResult.KeepOpen();
        }

        var action = ReadString(data, "action");
        if (action == CreateCodespaceActions.Open)
        {
            GitHubCodespace? codespace;
            lock (_lock)
            {
                codespace = _createdCodespace;
            }

            if (codespace is not null)
            {
                _browser.Open(codespace.WebUrl);
                return CommandResult.Dismiss();
            }
        }
        else if (action == CreateCodespaceActions.CreateAnother)
        {
            lock (_lock)
            {
                _createdCodespace = null;
                _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
            }

            RaiseItemsChanged();
        }
        else if (action == CreateCodespaceActions.Create)
        {
            var (repository, branch) = ReadInputs(inputs);
            if (repository is null || !IsRepositoryName(repository))
            {
                ShowForm(repository, branch, "Enter a repository as owner/name.");
            }
            else if (_auth.CurrentAccount is not { } account)
            {
                ShowForm(repository, branch, "Sign in to GitHub before creating a Codespace.");
            }
            else if (!account.Host.IsGitHubDotCom)
            {
                ShowForm(repository, branch, "Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com to create one.");
            }
            else
            {
                StartCreate(account, repository, branch);
            }
        }

        return CommandResult.KeepOpen();
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelCreate();
            _load.Dispose();
            _createdCodespace = null;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
        }

        IsLoading = false;
    }

    private void StartCreate(GitHubAccount account, string repository, string? branch)
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelCreate();
            _load.TryBegin(true, out operation);
            _createdCodespace = null;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Creating(repository));
        }

        _load.Publish(operation, () => IsLoading = true);
        _load.Publish(operation, () => RaiseItemsChanged());
        _load.Run(operation, () => CreateAsync(account, repository, branch, operation), () =>
        {
            lock (_lock)
            {
                if (_load.IsCurrent(operation) && _load.Error is { } error)
                {
                    _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(repository, branch, error));
                }
            }

            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        },
            "GitHub took too long to respond. Refresh codespaces before trying again.",
            area: DiagnosticArea.Codespaces, diagnosticEvent: DiagnosticEvent.CodespaceCreate,
            mutation: true, success: DiagnosticOutcome.Accepted);
    }

    private async Task CreateAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        ListLoadState.Operation operation)
    {
        var codespace = await _client.CreateCodespaceAsync(account, repository, branch, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation))
            {
                return;
            }

            _createdCodespace = codespace;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Created(codespace));
        }
    }

    private void ShowForm(string? repository, string? branch, string error)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(repository, branch, error));
        }

        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelCreate();
            _createdCodespace = null;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    private void CancelCreate()
    {
        _load.Invalidate();
    }

    private static bool IsRepositoryName(string repository)
    {
        var parts = repository.Split('/');
        return parts.Length == 2
            && parts.All(part => part.Length > 0 && !part.Any(char.IsWhiteSpace))
            && !parts.Any(part => part.Contains('\\'));
    }

    private static (string? Repository, string? Branch) ReadInputs(string inputs)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrEmpty(inputs) ? "{}" : inputs);
            var repository = ReadString(json.RootElement, "repository")?.Trim();
            var branch = ReadString(json.RootElement, "branch")?.Trim();
            return (repository, string.IsNullOrWhiteSpace(branch) ? null : branch);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            return ReadString(document.RootElement, name);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private sealed partial class CreateCodespaceForm : FormContent
    {
        private readonly CreateCodespacePage _page;

        public CreateCodespaceForm(CreateCodespacePage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.HandleSubmit(inputs, data);
    }
}
