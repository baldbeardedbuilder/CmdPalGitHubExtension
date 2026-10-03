// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class CreateAgentTaskPage : ContentPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.create-agent-task";

    private readonly AuthService _auth;
    private readonly IAgentsClient _client;
    private readonly GitHubRepository _repository;
    private readonly Lock _lock = new();
    private CreateAgentTaskForm _form;
    private AgentTaskRequest? _draft;
    private GitHubAccount? _reviewAccount;
    private HashSet<string>? _baselineTaskIds;
    private CancellationTokenSource? _operationCancellation;
    private Task _currentOperation = Task.CompletedTask;
    private bool _submitting;
    private bool _outcomeUnknown;
    private bool _priorOutcomeUnknown;
    private bool _disposed;
    private int _generation;

    public CreateAgentTaskPage(AuthService auth, IAgentsClient client, GitHubRepository repository)
    {
        _auth = auth;
        _client = client;
        _repository = repository;
        Id = $"{PageId}.{Uri.EscapeDataString(repository.FullName)}";
        Name = "Start Agent Task";
        Title = "Start Copilot task";
        Icon = Icons.Agents;
        _form = NewForm(null, null);
        _auth.AccountChanged += OnAccountChanged;
    }

    internal Task CurrentOperation
    {
        get
        {
            lock (_lock)
            {
                return _currentOperation;
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
        if (action == CreateAgentTaskActions.Review)
        {
            var draft = ReadDraft(inputs);
            if (draft is null)
            {
                ShowForm(_draft, "Enter a prompt for the agent.");
            }
            else if (_auth.CurrentAccount is not { } account)
            {
                ShowForm(draft, "Sign in to GitHub before starting a Copilot task.");
            }
            else
            {
                lock (_lock)
                {
                    if (!_disposed && _auth.CurrentAccount != account)
                    {
                        _draft = null;
                        _reviewAccount = null;
                        _form = NewForm(null, "Your GitHub account changed. Review a new task before submitting.");
                    }
                    else if (!_disposed)
                    {
                        _draft = draft;
                        _reviewAccount = account;
                        _outcomeUnknown = false;
                        _form = NewForm(CreateAgentTaskCards.Review(_repository.FullName, draft, _priorOutcomeUnknown));
                    }
                }

                RaiseItemsChanged();
            }
        }
        else if (action == CreateAgentTaskActions.Back)
        {
            lock (_lock)
            {
                if (!_disposed && !_submitting)
                {
                    _form = NewForm(CreateAgentTaskCards.Form(_repository.FullName, _draft, null));
                }
            }

            RaiseItemsChanged();
        }
        else if (action == CreateAgentTaskActions.Start)
        {
            StartSubmission();
        }
        else if (action == CreateAgentTaskActions.Check)
        {
            StartReconciliation();
        }
        else if (action == CreateAgentTaskActions.Edit)
        {
            lock (_lock)
            {
                if (!_disposed && _outcomeUnknown && !_submitting)
                {
                    _outcomeUnknown = false;
                    _form = NewForm(CreateAgentTaskCards.Form(
                        _repository.FullName,
                        _draft,
                        "A previous submission may still be running. Check repository tasks before submitting again."));
                }
            }

            RaiseItemsChanged();
        }

        return CommandResult.KeepOpen();
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            _disposed = true;
            CancelOperation();
        }
    }

    private void StartSubmission()
    {
        GitHubAccount? account;
        AgentTaskRequest? draft;
        CancellationToken token;
        int generation;
        lock (_lock)
        {
            if (_disposed || _submitting || _outcomeUnknown || _draft is null)
            {
                return;
            }

            account = _auth.CurrentAccount;
            if (account is null || account != _reviewAccount)
            {
                _draft = null;
                _reviewAccount = null;
                _form = NewForm(null, "Your GitHub account changed. Review a new task before submitting.");
                account = null;
                draft = null;
                token = default;
                generation = 0;
            }
            else
            {
                draft = _draft;
                CancelOperation();
                _operationCancellation = new CancellationTokenSource();
                token = _operationCancellation.Token;
                generation = ++_generation;
                _submitting = true;
                _baselineTaskIds = null;
                _form = NewForm(CreateAgentTaskCards.Starting(_repository.FullName));
            }
        }

        if (account is null || draft is null)
        {
            RaiseItemsChanged();
            return;
        }

        IsLoading = true;
        RaiseItemsChanged();
        lock (_lock)
        {
            _currentOperation = Task.Run(() => SubmitAsync(account, draft, generation, token));
        }
    }

    private async Task SubmitAsync(
        GitHubAccount account,
        AgentTaskRequest draft,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var existing = await _client.GetRepositoryTasksAsync(account, _repository.FullName, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!CanPublish(generation, cancellationToken))
                {
                    return;
                }

                _baselineTaskIds = existing.Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            }

            var task = await _client.StartTaskAsync(account, _repository.FullName, draft, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!CanPublish(generation, cancellationToken))
                {
                    return;
                }

                _draft = null;
                _reviewAccount = null;
                _baselineTaskIds = null;
                _outcomeUnknown = false;
                _priorOutcomeUnknown = false;
                _form = NewForm(CreateAgentTaskCards.Started(task));
            }
        }
        catch (AgentTaskOutcomeUnknownException)
        {
            try
            {
                await ReconcileAsync(account, generation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (!CanPublish(generation, cancellationToken))
                {
                    return;
                }

                _form = NewForm(CreateAgentTaskCards.Form(_repository.FullName, draft, ex.Message));
            }
        }
        finally
        {
            FinishOperation(generation);
        }
    }

    private void StartReconciliation()
    {
        GitHubAccount? account;
        CancellationToken token;
        int generation;
        lock (_lock)
        {
            if (_disposed || _submitting || !_outcomeUnknown || _baselineTaskIds is null)
            {
                return;
            }

            account = _auth.CurrentAccount;
            if (account is null)
            {
                return;
            }

            CancelOperation();
            _operationCancellation = new CancellationTokenSource();
            token = _operationCancellation.Token;
            generation = ++_generation;
            _submitting = true;
            _form = NewForm(CreateAgentTaskCards.Starting("Checking repository tasks..."));
        }

        IsLoading = true;
        RaiseItemsChanged();
        lock (_lock)
        {
            _currentOperation = Task.Run(() => ReconcileAndFinishAsync(account, generation, token));
        }
    }

    private async Task ReconcileAndFinishAsync(GitHubAccount account, int generation, CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(account, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            FinishOperation(generation);
        }
    }

    private async Task ReconcileAsync(GitHubAccount account, int generation, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitHubAgentTask>? candidates = null;
        bool couldNotCheck = false;
        try
        {
            var current = await _client.GetRepositoryTasksAsync(account, _repository.FullName, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!CanPublish(generation, cancellationToken))
                {
                    return;
                }

                candidates = current.Where(task => !_baselineTaskIds!.Contains(task.Id)).ToArray();
            }
        }
        catch (GitHubApiException)
        {
            couldNotCheck = true;
        }

        lock (_lock)
        {
            if (!CanPublish(generation, cancellationToken))
            {
                return;
            }

            _outcomeUnknown = true;
            _priorOutcomeUnknown = true;
            _form = NewForm(CreateAgentTaskCards.OutcomeUnknown(
                _repository.FullName, _repository.WebUrl, candidates, couldNotCheck));
        }
    }

    private void FinishOperation(int generation)
    {
        bool publish;
        lock (_lock)
        {
            publish = generation == _generation && !_disposed;
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

    private void ShowForm(AgentTaskRequest? draft, string error)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _draft = draft;
                _form = NewForm(CreateAgentTaskCards.Form(_repository.FullName, draft, error));
            }
        }

        RaiseItemsChanged();
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            CancelOperation();
            _draft = null;
            _reviewAccount = null;
            _baselineTaskIds = null;
            _submitting = false;
            _outcomeUnknown = false;
            _priorOutcomeUnknown = false;
            _form = NewForm(null, null);
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    private bool CanPublish(int generation, CancellationToken token) =>
        generation == _generation && !_disposed && !token.IsCancellationRequested;

    private void CancelOperation()
    {
        _generation++;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        _submitting = false;
    }

    private CreateAgentTaskForm NewForm(string? template, string? error = null) =>
        new(this, template ?? CreateAgentTaskCards.Form(_repository.FullName, _draft, error));

    private static AgentTaskRequest? ReadDraft(string inputs)
    {
        try
        {
            using var json = JsonDocument.Parse(string.IsNullOrEmpty(inputs) ? "{}" : inputs);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || ReadString(json.RootElement, "prompt") is not { } prompt
                || string.IsNullOrWhiteSpace(prompt))
            {
                return null;
            }

            var createPullRequest = json.RootElement.TryGetProperty("createPullRequest", out var toggle)
                && toggle.ValueKind == JsonValueKind.String
                && string.Equals(toggle.GetString(), "true", StringComparison.OrdinalIgnoreCase);
            return new AgentTaskRequest(
                prompt.Trim(),
                ReadString(json.RootElement, "model")?.Trim(),
                ReadString(json.RootElement, "customAgent")?.Trim(),
                ReadString(json.RootElement, "baseRef")?.Trim(),
                ReadString(json.RootElement, "headRef")?.Trim(),
                createPullRequest);
        }
        catch (JsonException)
        {
            return null;
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

    private sealed partial class CreateAgentTaskForm : FormContent
    {
        private readonly CreateAgentTaskPage _page;

        public CreateAgentTaskForm(CreateAgentTaskPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.HandleSubmit(inputs, data);
    }
}
