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
    private readonly Lock _lock = new();
    private CreateCodespaceForm _form;
    private GitHubCodespace? _createdCodespace;
    private GitHubAccount? _reviewAccount;
    private string? _reviewRepository;
    private string? _reviewBranch;
    private string? _confirmationId;
    private HashSet<string>? _baselineCodespaceNames;
    private CancellationTokenSource? _createCancellation;
    private Task _currentCreate = Task.CompletedTask;
    private int _generation;
    private bool _submitting;
    private bool _outcomeUnknown;
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
                return _currentCreate;
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
                if (_createdCodespace is not null && !_submitting)
                {
                    _createdCodespace = null;
                    _reviewAccount = null;
                    _reviewRepository = null;
                    _reviewBranch = null;
                    _confirmationId = null;
                    _baselineCodespaceNames = null;
                    _outcomeUnknown = false;
                    _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
                }
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
                PrepareConfirmation(account, repository, branch);
            }
        }
        else if (action == CreateCodespaceActions.Confirm)
        {
            StartCreate(ReadString(data, "confirmation"));
        }
        else if (action == CreateCodespaceActions.Back)
        {
            lock (_lock)
            {
                if (!_submitting && !_outcomeUnknown && ReadString(data, "confirmation") == _confirmationId)
                {
                    var repository = _reviewRepository;
                    var branch = _reviewBranch;
                    _reviewAccount = null;
                    _confirmationId = null;
                    _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(repository, branch, null));
                }
            }

            RaiseItemsChanged();
        }
        else if (action == CreateCodespaceActions.Check)
        {
            StartReconciliation();
        }

        return CommandResult.KeepOpen();
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            _disposed = true;
            CancelCreate();
        }
    }

    private void PrepareConfirmation(GitHubAccount account, string repository, string? branch)
    {
        lock (_lock)
        {
            if (_disposed || _submitting || _outcomeUnknown || !ReferenceEquals(_auth.CurrentAccount, account))
            {
                return;
            }

            _reviewAccount = account;
            _reviewRepository = repository;
            _reviewBranch = branch;
            _confirmationId = Guid.NewGuid().ToString();
            _form = new CreateCodespaceForm(this,
                CreateCodespaceCards.Confirm(repository, branch, account.Login, account.Host.Name, _confirmationId));
        }

        RaiseItemsChanged();
    }

    private void StartCreate(string? confirmation)
    {
        GitHubAccount account;
        string repository;
        string? branch;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed || _submitting || _outcomeUnknown || confirmation != _confirmationId
                || _reviewAccount is not { } reviewedAccount || !ReferenceEquals(_auth.CurrentAccount, reviewedAccount)
                || _reviewRepository is not { } reviewedRepository)
            {
                return;
            }

            CancelCreate();
            account = reviewedAccount;
            repository = reviewedRepository;
            branch = _reviewBranch;
            _createCancellation = new CancellationTokenSource();
            token = _createCancellation.Token;
            generation = ++_generation;
            _submitting = true;
            _baselineCodespaceNames = null;
            _createdCodespace = null;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Creating(repository));
        }

        IsLoading = true;
        RaiseItemsChanged();
        lock (_lock)
        {
            _currentCreate = Task.Run(() => CreateAsync(account, repository, branch, generation, token));
        }
    }

    private async Task CreateAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        int generation,
        CancellationToken cancellationToken)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceCreate, DiagnosticArea.Codespaces);
        Exception? failure = null;
        try
        {
            var baseline = await CodespaceListReader.GetAllAsync(_client, account, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!CanPublish(generation, account, cancellationToken))
                {
                    return;
                }

                _baselineCodespaceNames = baseline.Select(codespace => codespace.Name).ToHashSet(StringComparer.Ordinal);
            }

            var codespace = await _client.CreateCodespaceAsync(account, repository, branch, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!CanPublish(generation, account, cancellationToken))
                {
                    return;
                }

                _createdCodespace = codespace;
                _reviewAccount = null;
                _outcomeUnknown = false;
                _form = new CreateCodespaceForm(this, CreateCodespaceCards.Created(codespace));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
            if (OperationDiagnostics.FailureOutcome(ex) == DiagnosticOutcome.Unknown)
            {
                await ReconcileCreatedCodespaceAsync(account, repository, branch, generation, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                lock (_lock)
                {
                    if (!CanPublish(generation, account, cancellationToken))
                    {
                        return;
                    }

                    _reviewAccount = null;
                    _confirmationId = null;
                    _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(repository, branch, ex.Message));
                }
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                publish = generation == _generation && !_disposed && ReferenceEquals(_auth.CurrentAccount, account);
                if (publish)
                {
                    _submitting = false;
                }
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }

            lock (_lock)
            {
                publish = generation == _generation && !_disposed && ReferenceEquals(_auth.CurrentAccount, account);
            }

            PageDiagnostics.Finish(operation, failure, publish, DiagnosticOutcome.Accepted, mutation: true, cancellationToken: cancellationToken);
        }
    }

    private void StartReconciliation()
    {
        GitHubAccount account;
        string repository;
        string? branch;
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed || _submitting || !_outcomeUnknown || _reviewAccount is not { } reviewedAccount
                || !ReferenceEquals(_auth.CurrentAccount, reviewedAccount) || _reviewRepository is not { } reviewedRepository)
            {
                return;
            }

            CancelCreate();
            account = reviewedAccount;
            repository = reviewedRepository;
            branch = _reviewBranch;
            _createCancellation = new CancellationTokenSource();
            token = _createCancellation.Token;
            generation = ++_generation;
            _submitting = true;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.OutcomeUnknown(repository, branch));
        }

        IsLoading = true;
        RaiseItemsChanged();
        lock (_lock)
        {
            _currentCreate = Task.Run(async () =>
            {
                try
                {
                    await ReconcileCreatedCodespaceAsync(account, repository, branch, generation, token).ConfigureAwait(false);
                }
                finally
                {
                    bool publish;
                    lock (_lock)
                    {
                        publish = CanPublish(generation, account, token);
                        if (publish)
                        {
                            _submitting = false;
                        }
                    }

                    if (publish)
                    {
                        IsLoading = false;
                        RaiseItemsChanged();
                    }
                }
            });
        }
    }

    private async Task ReconcileCreatedCodespaceAsync(
        GitHubAccount account,
        string repository,
        string? branch,
        int generation,
        CancellationToken cancellationToken)
    {
        GitHubCodespace[] candidates = [];
        string? reconciliationError = null;
        try
        {
            var current = await CodespaceListReader.GetAllAsync(_client, account, cancellationToken).ConfigureAwait(false);
            HashSet<string> baseline;
            lock (_lock)
            {
                if (!CanPublish(generation, account, cancellationToken))
                {
                    return;
                }

                baseline = _baselineCodespaceNames ?? [];
            }

            candidates = current.Where(codespace => !baseline.Contains(codespace.Name)
                && string.Equals(codespace.RepositoryFullName, repository, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(branch) || string.Equals(codespace.Branch, branch, StringComparison.Ordinal)))
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            reconciliationError = $"Couldn't verify the request. {ex.Message}";
        }

        lock (_lock)
        {
            if (!CanPublish(generation, account, cancellationToken))
            {
                return;
            }

            if (candidates.Length == 1)
            {
                _createdCodespace = candidates[0];
                _outcomeUnknown = false;
                _reviewAccount = null;
                _form = new CreateCodespaceForm(this, CreateCodespaceCards.Created(candidates[0]));
            }
            else
            {
                _outcomeUnknown = true;
                _reviewAccount = account;
                _form = new CreateCodespaceForm(this, CreateCodespaceCards.OutcomeUnknown(repository, branch,
                    candidates.Length > 1 ? "More than one matching new Codespace was found." : reconciliationError));
            }
        }
    }

    private void ShowForm(string? repository, string? branch, string error)
    {
        lock (_lock)
        {
            _reviewAccount = null;
            _reviewRepository = repository;
            _reviewBranch = branch;
            _confirmationId = null;
            _outcomeUnknown = false;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(repository, branch, error));
        }

        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            CancelCreate();
            _createdCodespace = null;
            _reviewAccount = null;
            _reviewRepository = null;
            _reviewBranch = null;
            _confirmationId = null;
            _baselineCodespaceNames = null;
            _outcomeUnknown = false;
            _form = new CreateCodespaceForm(this, CreateCodespaceCards.Form(null, null, null));
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    private void CancelCreate()
    {
        _generation++;
        _createCancellation?.Cancel();
        _createCancellation?.Dispose();
        _createCancellation = null;
        _submitting = false;
    }

    private bool CanPublish(int generation, GitHubAccount account, CancellationToken cancellationToken) =>
        generation == _generation && !_disposed && !cancellationToken.IsCancellationRequested
        && ReferenceEquals(_auth.CurrentAccount, account);

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
