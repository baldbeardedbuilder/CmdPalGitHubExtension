// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class WorkflowDispatchPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IActionsClient _client;
    private readonly string _repository;
    private readonly ActionsPage? _parent;
    private readonly GitHubAccount? _account;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FormContent _form;
    private GitHubWorkflow[] _workflows = [];
    private IReadOnlyList<string> _refs = [];
    private GitHubWorkflow? _selectedWorkflow;
    private string? _selectedRef;
    private WorkflowDispatchDefinition? _definition;
    private DispatchReview? _review;
    private Task _currentOperation = Task.CompletedTask;
    private string? _message;
    private bool _loading;
    private bool _busy;
    private bool _submitted;
    private bool _disposed;
    private bool _loadedChoices;

    internal WorkflowDispatchPage(AuthService auth, IActionsClient client, string repository, ActionsPage? parent = null)
    {
        _auth = auth;
        _client = client;
        _repository = repository;
        _parent = parent;
        _account = auth.CurrentAccount;
        Name = "Run workflow manually";
        Title = $"Dispatch workflow in {repository}";
        Icon = Icons.Actions;
        _form = new DispatchForm(this, WorkflowDispatchCards.Loading(repository));
        _accountSubscription = auth.Subscribe(this, static page => page.Dispose());
    }

    internal Task CurrentOperation
    {
        get { lock (_lock) return _currentOperation; }
    }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            if (!_loading && !_loadedChoices && !_disposed)
            {
                _loading = true;
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => LoadChoicesAsync(token));
            }

            return [_form];
        }
    }

    internal CommandResult Submit(string inputs, string data)
    {
        var action = ReadAction(data);
        lock (_lock)
        {
            if (_disposed || _busy || _submitted || _account is null || _auth.CurrentAccount != _account)
            {
                return CommandResult.KeepOpen();
            }

            if (action == "reload")
            {
                _workflows = [];
                _refs = [];
                _message = null;
                _loadedChoices = false;
                _loading = true;
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => LoadChoicesAsync(token));
            }
            else if (action == "load-inputs")
            {
                var selected = ParseSelection(inputs);
                if (selected is null)
                {
                    _message = "Choose a workflow and branch.";
                    _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                        _selectedWorkflow?.Id, _selectedRef, _definition, _message));
                    RaiseLater();
                    return CommandResult.KeepOpen();
                }

                _selectedWorkflow = selected.Value.Workflow;
                _selectedRef = selected.Value.Ref;
                _busy = true;
                _loading = true;
                _message = "Reading this workflow's dispatch definition...";
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => LoadDefinitionAsync(selected.Value.Workflow, selected.Value.Ref, token));
            }
            else if (action == "review" && _definition is not null && _selectedWorkflow is not null && _selectedRef is not null)
            {
                var selection = ParseSelection(inputs);
                if (selection is null)
                {
                    _message = "Choose a workflow and branch.";
                    _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                        _selectedWorkflow.Id, _selectedRef, _definition, _message));
                    RaiseLater();
                    return CommandResult.KeepOpen();
                }

                if (selection.Value.Workflow.Id != _selectedWorkflow.Id || selection.Value.Ref != _selectedRef)
                {
                    _selectedWorkflow = selection.Value.Workflow;
                    _selectedRef = selection.Value.Ref;
                    _definition = null;
                    _message = "The workflow or branch changed. Load its input definitions before reviewing the dispatch.";
                    _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                        _selectedWorkflow.Id, _selectedRef, null, _message));
                    RaiseLater();
                    return CommandResult.KeepOpen();
                }

                try
                {
                    var values = ReadInputs(inputs, _definition);
                    var normalized = _definition.Validate(values);
                    _review = new DispatchReview(_selectedWorkflow, _selectedRef, _definition, normalized);
                    _message = null;
                    _form = new DispatchForm(this, WorkflowDispatchCards.Confirm(_repository, _review));
                }
                catch (GitHubApiException ex)
                {
                    _message = ex.Message;
                    _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                        _selectedWorkflow.Id, _selectedRef, _definition, _message));
                }

                RaiseLater();
            }
            else if (action == "cancel-review" && _review is not null)
            {
                _review = null;
                _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                    _selectedWorkflow?.Id, _selectedRef, _definition, null));
                RaiseLater();
            }
            else if (action == "dispatch" && _review is not null)
            {
                _busy = true;
                _loading = true;
                _message = "Verifying the workflow definition and repository access...";
                var token = _lifetime.Token;
                var review = _review;
                _currentOperation = Task.Run(() => DispatchAsync(review, token));
            }
        }

        if (_busy)
        {
            IsLoading = true;
            RaiseItemsChanged();
        }

        return CommandResult.KeepOpen();
    }

    private async Task LoadChoicesAsync(CancellationToken token)
    {
        try
        {
            var contextTask = _client.GetDispatchContextAsync(_account!, _repository, token);
            var firstWorkflowsTask = _client.GetWorkflowsAsync(_account!, _repository, null, token);
            await Task.WhenAll(contextTask, firstWorkflowsTask).ConfigureAwait(false);
            var firstWorkflows = await firstWorkflowsTask.ConfigureAwait(false);
            var workflows = firstWorkflows.Workflows.ToList();
            var visited = new HashSet<Uri>();
            var next = firstWorkflows.NextPage;
            while (next is not null)
            {
                if (!visited.Add(next))
                {
                    throw new GitHubApiException("GitHub returned a repeated workflow list page.");
                }

                var page = await _client.GetWorkflowsAsync(_account!, _repository, next, token).ConfigureAwait(false);
                workflows.AddRange(page.Workflows);
                next = page.NextPage;
                if (workflows.Count > 200)
                {
                    throw new GitHubApiException("This repository has too many workflows to list here. Open Actions on GitHub.");
                }
            }

            var context = await contextTask.ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || token.IsCancellationRequested || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _workflows = workflows.Where(workflow => workflow.State == "active").ToArray();
                _refs = context.Refs;
                _loadedChoices = true;
                _selectedWorkflow = _workflows.Length > 0 ? _workflows[0] : null;
                _selectedRef = context.DefaultRef;
                _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                    _selectedWorkflow?.Id, _selectedRef, null,
                    _workflows.Length == 0 ? "No active workflows are available in this repository." : null));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (!_disposed && _auth.CurrentAccount == _account)
                {
                    _loadedChoices = true;
                    _message = ex.Message;
                    _form = new DispatchForm(this, WorkflowDispatchCards.Error(_repository, ex.Message));
                }
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                _loading = false;
                publish = !_disposed && !token.IsCancellationRequested && _auth.CurrentAccount == _account;
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private async Task LoadDefinitionAsync(GitHubWorkflow workflow, string @ref, CancellationToken token)
    {
        try
        {
            var definition = await _client.GetDispatchDefinitionAsync(_account!, _repository, workflow.Path, @ref, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || token.IsCancellationRequested || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _definition = definition;
                _message = null;
                _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                    workflow.Id, @ref, definition, null));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (!_disposed && _auth.CurrentAccount == _account)
                {
                    _definition = null;
                    _message = ex.Message;
                    _form = new DispatchForm(this, WorkflowDispatchCards.Form(_repository, _workflows, _refs,
                        workflow.Id, @ref, null, ex.Message));
                }
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                _busy = false;
                _loading = false;
                publish = !_disposed && !token.IsCancellationRequested && _auth.CurrentAccount == _account;
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private async Task DispatchAsync(DispatchReview review, CancellationToken token)
    {
        var message = "GitHub accepted the workflow dispatch request. Refresh Actions to check for the new run.";
        try
        {
            var latest = await _client.GetDispatchDefinitionAsync(_account!, _repository, review.Workflow.Path, review.Ref, token)
                .ConfigureAwait(false);
            if (latest.Inputs.Count != review.Definition.Inputs.Count
                || latest.Inputs.Where((input, index) =>
                    input.Name != review.Definition.Inputs[index].Name
                    || input.Description != review.Definition.Inputs[index].Description
                    || input.Type != review.Definition.Inputs[index].Type
                    || input.Required != review.Definition.Inputs[index].Required
                    || input.DefaultValue != review.Definition.Inputs[index].DefaultValue
                    || !input.Options.SequenceEqual(review.Definition.Inputs[index].Options, StringComparer.Ordinal)).Any())
            {
                throw new GitHubApiException("The workflow inputs changed after you reviewed them. Reopen dispatch and review the current definition.");
            }

            var values = latest.Validate(review.Inputs.ToDictionary(
                static pair => pair.Key,
                static pair => (string?)pair.Value,
                StringComparer.Ordinal));
            if (!await _client.CanCancelAsync(_account!, _repository, token).ConfigureAwait(false))
            {
                throw new GitHubApiException("You need repository write access and Actions write permission to dispatch a workflow.");
            }

            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_disposed || _auth.CurrentAccount != _account)
                {
                    return;
                }
            }

            await _client.DispatchAsync(_account!, _repository, review.Workflow.Id, review.Ref, values, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || token.IsCancellationRequested || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _submitted = true;
            }

            if (_parent is not null)
            {
                await _parent.RefreshAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubApiException ex)
        {
            message = ex.OutcomeUnknown
                ? $"The dispatch request may have been accepted. Refresh Actions before trying again. {ex.Message}"
                : ex.Message;
            if (ex.OutcomeUnknown)
            {
                lock (_lock)
                {
                    if (!_disposed && !token.IsCancellationRequested && _auth.CurrentAccount == _account)
                    {
                        _submitted = true;
                    }
                }
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                _busy = false;
                _loading = false;
                publish = !_disposed && !token.IsCancellationRequested && _auth.CurrentAccount == _account;
                if (!_disposed && _auth.CurrentAccount == _account)
                {
                    _message = message;
                    _review = null;
                    _form = new DispatchForm(this, WorkflowDispatchCards.Result(
                        _repository, message, _submitted, _account?.Host.WebUrl is { } host
                            ? new Uri(host, $"repos/{_repository}/actions")
                            : null));
                }
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private (GitHubWorkflow Workflow, string Ref)? ParseSelection(string inputs)
    {
        Dictionary<string, string> values;
        try
        {
            values = ParseObject(inputs);
        }
        catch (JsonException)
        {
            return null;
        }

        var id = values.TryGetValue("workflow", out var workflowValue)
            && long.TryParse(workflowValue, out var workflowId) ? workflowId : 0;
        var @ref = values.TryGetValue("dispatchRef", out var refValue) ? refValue.Trim() : string.Empty;
        var workflow = _workflows.FirstOrDefault(candidate => candidate.Id == id);
        return workflow is null || string.IsNullOrWhiteSpace(@ref) || !_refs.Contains(@ref, StringComparer.Ordinal)
            ? null
            : (workflow, @ref);
    }

    private static Dictionary<string, string?> ReadInputs(string json, WorkflowDispatchDefinition definition)
    {
        var values = ParseObject(json);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var input in definition.Inputs)
        {
            if (values.TryGetValue($"input_{input.Name}", out var value))
            {
                result[input.Name] = value;
            }
        }

        return result;
    }

    private static Dictionary<string, string> ParseObject(string json)
    {
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubApiException("Choose a workflow and branch, then try again.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                values[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return values;
    }

    private static string? ReadAction(string data)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("action", out var action)
                && action.ValueKind == JsonValueKind.String ? action.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RaiseLater() => Task.Run(() =>
    {
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount != _account)
            {
                return;
            }
        }

        RaiseItemsChanged();
    });

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
        }

        _accountSubscription.Dispose();
        _lifetime.Dispose();
    }

    internal sealed record DispatchReview(
        GitHubWorkflow Workflow, string Ref, WorkflowDispatchDefinition Definition, Dictionary<string, string> Inputs);

    private sealed partial class DispatchForm(WorkflowDispatchPage page, string template) : FormContent
    {
        public override string TemplateJson { get; set; } = template;
        public override ICommandResult SubmitForm(string inputs, string data) => page.Submit(inputs, data);
    }
}

internal static class WorkflowDispatchCards
{
    internal static string Loading(string repository) => Card(
        Text($"Run a workflow in {repository}", "Large"),
        Text("Loading active workflows and repository branches..."));

    internal static string Error(string repository, string message) => Card(
        Text($"Run a workflow in {repository}", "Large"),
        Text(message),
        """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Load choices","data":{"action":"reload"}}]}""");

    internal static string Form(
        string repository,
        IReadOnlyList<GitHubWorkflow> workflows,
        IReadOnlyList<string> refs,
        long? workflowId,
        string? @ref,
        WorkflowDispatchDefinition? definition,
        string? message)
    {
        var body = new List<string>
        {
            Text($"Run a workflow in {repository}", "Large"),
            Text("Choose a workflow and an existing branch. Command Palette reads workflow_dispatch inputs from the workflow file before enabling dispatch."),
            message is null ? string.Empty : Text(message, attention: true),
        };
        if (workflows.Count > 0)
        {
            var selectedWorkflow = workflowId ?? workflows[0].Id;
            body.Add(Choices("workflow", "Workflow", selectedWorkflow.ToString(System.Globalization.CultureInfo.InvariantCulture),
                workflows.Select(workflow => (workflow.Name, workflow.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)))));
        }

        if (refs.Count > 0)
        {
            var selectedRef = @ref ?? refs[0];
            body.Add($$"""{"type":"Input.Text","id":"dispatchRef","label":"Branch","value":{{GitHubJson.String(selectedRef)}}}""");
        }

        if (definition is not null)
        {
            body.Add(Text("Workflow inputs", null));
            body.AddRange(definition.Inputs.Select(Input));
            body.Add("""{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Review dispatch","style":"positive","data":{"action":"review"}}]}""");
        }
        else if (workflows.Count > 0 && refs.Count > 0)
        {
            body.Add("""{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Load inputs","data":{"action":"load-inputs"}}]}""");
        }

        return Card([.. body]);
    }

    internal static string Confirm(string repository, WorkflowDispatchPage.DispatchReview review)
    {
        var shownInputs = review.Inputs.Select(pair =>
        {
            var input = review.Definition.Inputs.Single(definition => definition.Name == pair.Key);
            var source = pair.Value == input.DefaultValue ? " (default)" : string.Empty;
            return Text($"{pair.Key}: {pair.Value}{source}");
        });

        return Card(
        [
            Text("Confirm workflow dispatch", "Large"),
            Text($"Repository: {repository}\nWorkflow: {review.Workflow.Name}\nRef: {review.Ref}"),
            Text("This starts GitHub Actions compute and may incur charges. The values below are sent to GitHub."),
            .. shownInputs,
            """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Dispatch workflow","style":"positive","data":{"action":"dispatch"}},{"type":"Action.Submit","title":"Edit inputs","data":{"action":"cancel-review"}}]}""",
        ]);
    }

    internal static string Result(string repository, string message, bool submitted, Uri? actionsUrl) => Card(
        Text($"Workflow dispatch for {repository}", "Large"),
        Text(message),
        submitted
            ? $$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Open Actions on GitHub","url":{{GitHubJson.String((actionsUrl ?? new Uri("https://github.com")).AbsoluteUri)}}}]}"""
            : string.Empty);

    private static string Input(WorkflowInputDefinition input)
    {
        var id = $"input_{input.Name}";
        var required = input.Required ? ",\"isRequired\":true,\"errorMessage\":\"This input is required\"" : string.Empty;
        var description = input.Description.Length == 0 ? string.Empty : Text(input.Description);
        var value = GitHubJson.String(input.DefaultValue);
        var control = input.Type switch
        {
            "choice" => Choices(id, input.Name, input.DefaultValue, input.Options.Select(option => (option, option))),
            "boolean" => Choices(id, input.Name, input.DefaultValue, [("true", "true"), ("false", "false")]),
            _ => $$"""{"type":"Input.Text","id":{{GitHubJson.String(id)}},"label":{{GitHubJson.String(input.Name)}},"value":{{value}}{{required}}}""",
        };
        if (input.Required && input.Type == "choice")
        {
            control = control.Replace("\"choices\":", "\"isRequired\":true,\"errorMessage\":\"Choose a value\",\"choices\":", StringComparison.Ordinal);
        }

        return description.Length == 0 ? control : control + "," + description;
    }

    private static string Choices(string id, string label, string value, IEnumerable<(string Title, string Value)> choices) =>
        $$"""{"type":"Input.ChoiceSet","id":{{GitHubJson.String(id)}},"label":{{GitHubJson.String(label)}},"value":{{GitHubJson.String(value)}},"choices":[{{string.Join(',', choices.Select(choice => $$"""{"title":{{GitHubJson.String(choice.Title)}},"value":{{GitHubJson.String(choice.Value)}}}"""))}}]}""";

    private static string Text(string text, string? size = null, bool attention = false) => $$"""
        {"type":"TextBlock","text":{{GitHubJson.String(text)}},"wrap":true{{(size is null ? string.Empty : $",\"size\":\"{size}\",\"weight\":\"Bolder\"")}}{{(attention ? ",\"color\":\"Attention\"" : string.Empty)}}}
        """;

    private static string Card(params string[] body) => $$"""
        {"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(',', body.Where(item => item.Length > 0))}}]}
        """;
}
