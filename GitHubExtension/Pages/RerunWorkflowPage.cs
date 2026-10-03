// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class RerunWorkflowPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IActionsClient _client;
    private readonly ActionsPage _parent;
    private readonly string _repository;
    private readonly GitHubAccount? _account;
    private readonly GitHubWorkflowRun _original;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _cancellation = new();
    private FormContent _form;
    private Task _currentOperation = Task.CompletedTask;
    private bool _busy;
    private bool _submitted;
    private bool _accepted;
    private bool _invalid;

    internal RerunWorkflowPage(AuthService auth, IActionsClient client, ActionsPage parent, string repository, GitHubWorkflowRun run)
    {
        _auth = auth;
        _client = client;
        _parent = parent;
        _repository = repository;
        _account = auth.CurrentAccount;
        _original = run;
        Id = $"{ActionsPage.PageId}.rerun.{Uri.EscapeDataString(repository)}.{run.Id}.{run.RunAttempt}";
        Name = "Rerun workflow...";
        Title = $"Rerun {run.Name}";
        Icon = Icons.Actions;
        _form = new RerunForm(this, RerunWorkflowCards.Confirm(repository, run));
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
        try
        {
            using var action = JsonDocument.Parse(data);
            var refresh = action.RootElement.GetProperty("action").GetString() == "refresh";
            var confirm = action.RootElement.GetProperty("action").GetString() == "rerun";
            if (!refresh && !confirm)
            {
                return CommandResult.KeepOpen();
            }

            var failedOnly = false;
            var debug = false;
            if (confirm)
            {
                using var values = JsonDocument.Parse(inputs);
                failedOnly = values.RootElement.TryGetProperty("jobs", out var jobs) && jobs.GetString() == "failed";
                debug = values.RootElement.TryGetProperty("debug", out var logging) && logging.GetString() == "true";
                if (!_original.CanRerun || (failedOnly && !_original.CanRerunFailed))
                {
                    return CommandResult.KeepOpen();
                }
            }

            lock (_lock)
            {
                if (_busy || _invalid || _account is null || _auth.CurrentAccount != _account || (confirm && _submitted))
                {
                    return CommandResult.KeepOpen();
                }

                _busy = true;
                _currentOperation = Task.Run(() => OperateAsync(refresh, failedOnly, debug, _cancellation.Token));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
        }

        return CommandResult.KeepOpen();
    }

    private async Task OperateAsync(bool refresh, bool failedOnly, bool debug, CancellationToken token)
    {
        var message = "Checking workflow run...";
        Show(message);
        try
        {
            var run = await _client.GetRunAsync(_account!, _repository, _original.Id, token).ConfigureAwait(false);
            if (!refresh)
            {
                if (!run.CanRerun || (failedOnly && !run.CanRerunFailed) || run.RunAttempt != _original.RunAttempt)
                {
                    throw new GitHubApiException("The run's state or attempt changed. Refresh the Actions list and confirm again.");
                }

                lock (_lock)
                {
                    if (_invalid || _auth.CurrentAccount != _account)
                    {
                        return;
                    }

                    _submitted = true;
                }

                token.ThrowIfCancellationRequested();
                await _client.RerunAsync(_account!, _repository, run.Id, failedOnly, debug, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                _accepted = true;
                message = "Rerun requested. Waiting for GitHub to report the new attempt.";
                run = await _client.GetRunAsync(_account!, _repository, _original.Id, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            if (_accepted)
            {
                message = run.RunAttempt is { } attempt && _original.RunAttempt is { } previous && attempt > previous
                    ? $"Rerun requested. Attempt {attempt}: {WorkflowRunFormatting.State(run)}."
                    : "Rerun requested. Waiting for GitHub to report the new attempt.";
            }
            else
            {
                message = $"Attempt {run.RunAttempt?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "unknown"}: {WorkflowRunFormatting.State(run)}.";
            }

            if (IsCurrent())
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
            message = _accepted
                ? $"Rerun requested, but status couldn't be refreshed. {ex.Message}"
                : _submitted
                    ? $"The rerun request wasn't confirmed. It may have been accepted. Refresh before trying again. {ex.Message}"
                    : ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                _busy = false;
            }

            Show(message);
        }
    }

    private void Show(string message)
    {
        lock (_lock)
        {
            if (_invalid)
            {
                return;
            }

            _form = new RerunForm(this, RerunWorkflowCards.Status(_repository, _original.Id, message));
        }

        RaiseItemsChanged();
    }

    private bool IsCurrent()
    {
        lock (_lock)
        {
            return !_invalid && _auth.CurrentAccount == _account;
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_invalid)
            {
                return;
            }

            _invalid = true;
            _form = new RerunForm(this, RerunWorkflowCards.Status(_repository, _original.Id, "Account changed or page closed. Reopen Actions to continue."));
        }

        _auth.AccountChanged -= OnAccountChanged;
        _cancellation.Cancel();
        RaiseItemsChanged();
    }

    private sealed partial class RerunForm(RerunWorkflowPage page, string template) : FormContent
    {
        public override string TemplateJson { get; set; } = template;

        public override ICommandResult SubmitForm(string inputs, string data) => page.HandleSubmit(inputs, data);
    }
}
