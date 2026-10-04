// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

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
    private readonly MutationExecutor _mutations;
    private (GitHubAccount Account, string Repository, string? Branch)? _review;
    private bool _disposed => _load.Disposed;
    private bool _creating;
    private GitHubAccount? _unknownAccount;

    public CreateCodespacePage(AuthService auth, ICodespacesClient client, IBrowserLauncher browser)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _mutations = new MutationExecutor(auth);
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

    private CommandResult HandleSubmit(CreateCodespaceForm source, string inputs, string data)
    {
        lock (_lock)
        {
            if (_disposed || _creating || _unknownAccount is not null || !ReferenceEquals(source, _form))
            {
                return CommandResult.KeepOpen();
            }
        }

        var action = ReadString(data, "action");
        if (action == "cancel")
        {
            lock (_lock)
            {
                if (_review is { } review)
                {
                    _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(review.Repository, review.Branch, null));
                    _review = null;
                }
            }

            RaiseItemsChanged();
        }
        else if (action == CreateCodespaceActions.Confirm)
        {
            (GitHubAccount Account, string Repository, string? Branch)? review;
            lock (_lock)
            {
                // Check identity while capturing the review, not before releasing the lock.
                if (!ReferenceEquals(source, _form))
                {
                    return CommandResult.KeepOpen();
                }

                review = _review;
                _review = null;
            }
            if (review is { } captured && _mutations.IsCurrent(captured.Account))
            {
                StartCreate(captured.Account, captured.Repository, captured.Branch);
            }
        }
        else if (action == CreateCodespaceActions.Open)
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
            lock (_lock)
            {
                if (_unknownAccount is not null)
                {
                    return CommandResult.KeepOpen();
                }
            }

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
                lock (_lock)
                {
                    _review = (account, repository, branch);
                    _form = new CreateCodespaceForm(this, MutationConfirmation.Card(
                        account, "Create Codespace", $"{repository} ({branch ?? "default branch"})",
                        "Creating a Codespace uses compute and storage and may incur charges. Review the repository and branch before confirming.",
                        CreateCodespaceActions.Confirm));
                }

                RaiseItemsChanged();
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

            _load.Dispose();
        }
        _mutations.Dispose();
        IsLoading = false;
    }

    private void StartCreate(GitHubAccount account, string repository, string? branch)
    {
        ListLoadState.Operation request;
        lock (_lock)
        {
            if (_disposed || _creating || !_mutations.IsCurrent(account))
            {
                return;
            }

            _load.TryBegin(true, out request);
            _creating = true;
            _createdCodespace = null;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Creating(repository));
        }

        _load.Publish(request, () => IsLoading = true);
        _load.Publish(request, () => RaiseItemsChanged());
        _load.Run(request, () => CreateAsync(account, repository, branch, request), () => { },
            "GitHub took too long to respond. Check GitHub before creating another Codespace.",
            area: DiagnosticArea.Codespaces, mutation: true, diagnose: false);
    }

    private async Task CreateAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        ListLoadState.Operation request)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceCreate, DiagnosticArea.Codespaces);
        Exception? failure = null;
        try
        {
            var result = await _mutations.ExecuteAsync(
                account, "create-codespace",
                _ => Task.FromResult(account.Host.IsGitHubDotCom && IsRepositoryName(repository)),
                async token =>
                {
                    GitHubCodespace codespace;
                    try
                    {
                        codespace = await _client.CreateCodespaceAsync(account, repository, branch, token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        throw;
                    }
                    if (!string.Equals(codespace.RepositoryFullName, repository, StringComparison.OrdinalIgnoreCase)
                        || (branch is not null && !string.Equals(codespace.Branch, branch, StringComparison.Ordinal)))
                    {
                        return new MutationResult<GitHubCodespace>(MutationState.Unknown, Error: "GitHub returned a different repository or branch. Check GitHub before creating another Codespace.");
                    }

                    return new MutationResult<GitHubCodespace>(MutationState.Completed, codespace);
                }, cancellationToken: request.Token).ConfigureAwait(false);
            var outcome = result.State switch
            {
                MutationState.Completed => DiagnosticOutcome.Accepted,
                MutationState.Pending => DiagnosticOutcome.Accepted,
                MutationState.Unknown => DiagnosticOutcome.Unknown,
                MutationState.Stale => DiagnosticOutcome.Cancelled,
                _ => DiagnosticOutcome.Failed,
            };
            if (failure is not null && result.State != MutationState.Stale)
            {
                operation.Fail(failure, outcome: outcome);
            }
            else
            {
                operation.Complete(operation.ChildOutcome == outcome ? null : outcome);
            }
            lock (_lock)
            {
                if (!_load.IsCurrent(request) || result.State == MutationState.Stale || !_mutations.IsCurrent(account))
                {
                    return;
                }

                if (result.State == MutationState.Completed && result.Value is { } created)
                {
                    _createdCodespace = created;
                    _form = new CreateCodespaceForm(this, CreateCodespaceCards.Created(created));
                }
                else
                {
                    var error = result.Error ?? "GitHub is still processing this request. Check GitHub before retrying.";
                    _unknownAccount = result.State is MutationState.Unknown or MutationState.Pending ? account : null;
                    _form = new CreateCodespaceForm(this, result.State is MutationState.Unknown or MutationState.Pending
                        ? CreateCodespaceCards.Unknown(error, result.AuthorizeUrl)
                        : CreateCodespaceCards.Form(repository, branch, error, result.AuthorizeUrl));
                }
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                publish = _load.IsCurrent(request);
                if (publish)
                {
                    _creating = false;
                }
            }

            if (publish)
            {
                _load.Publish(request, () => IsLoading = false);
                _load.Publish(request, () => RaiseItemsChanged());
            }
        }
    }

    private void ShowForm(string? repository, string? branch, string error)
    {
        lock (_lock)
        {
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

            _load.Invalidate(reset: true);
            _review = null;
            _creating = false;
            _unknownAccount = null;
            _createdCodespace = null;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
        }

        IsLoading = false;
        RaiseItemsChanged();
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

        public override ICommandResult SubmitForm(string inputs, string data)
        {
            lock (_page._lock)
            {
                if (!ReferenceEquals(this, _page._form))
                {
                    return CommandResult.KeepOpen();
                }
            }

            return _page.HandleSubmit(this, inputs, data);
        }
    }
}
