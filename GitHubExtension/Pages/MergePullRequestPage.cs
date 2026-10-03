// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class MergePullRequestPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IPullRequestMergeClient _client;
    private readonly GitHubAccount _account;
    private readonly string _repository;
    private readonly int _number;
    private readonly Uri _webUrl;
    private readonly Lock _lock = new();
    private string _confirmationId = Guid.NewGuid().ToString();
    private CancellationTokenSource _cancellation = new();
    private MergeForm _form;
    private PullRequestMergeTarget? _target;
    private PullRequestMergeResult? _result;
    private Task _currentWork = Task.CompletedTask;
    private bool _started;
    private bool _busy;
    private bool _submitted;
    private bool _invalidated;
    private bool _disposed;

    public MergePullRequestPage(
        AuthService auth, IPullRequestMergeClient client, GitHubAccount account, string repository, int number, Uri webUrl)
    {
        _auth = auth;
        _client = client;
        _account = account;
        _repository = repository;
        _number = number;
        _webUrl = webUrl;
        Id = $"com.baldbeardedbuilder.cmdpal.github.merge.{Guid.NewGuid()}";
        Name = "Merge pull request";
        Title = $"Merge {repository}#{number}";
        Icon = Icons.PullRequests;
        _form = new MergeForm(this, Card("Loading merge confirmation...", null));
        _auth.AccountChanged += OnAccountChanged;
    }

    internal Task CurrentWork
    {
        get
        {
            lock (_lock) return _currentWork;
        }
    }

    public override IContent[] GetContent()
    {
        StartWork("load", null);
        lock (_lock) return [_form];
    }

    internal ICommandResult HandleSubmit(string inputs, string data)
    {
        var action = ReadString(data, "action");
        var confirmation = ReadString(data, "confirmation");
        if (action == "cancel" && confirmation is not null)
        {
            Invalidate("Stopped locally. An already submitted merge or queue entry is not cancelled. Check GitHub for its outcome.",
                allowFresh: true, expectedConfirmation: confirmation);
        }
        else if (confirmation is not null)
        {
            if (action == "prepare")
            {
                StartNewConfirmation(confirmation);
            }
            else if (action != "confirm" || ReadString(inputs, "scopeAccepted") == "true")
            {
                StartWork(action, ReadString(inputs, "method"), confirmation);
            }
        }

        return CommandResult.KeepOpen();
    }

    private void StartNewConfirmation(string confirmation)
    {
        lock (_lock)
        {
            if (_disposed || _busy || confirmation != _confirmationId || !ReferenceEquals(_auth.CurrentAccount, _account)) return;
            _cancellation.Dispose();
            _cancellation = new CancellationTokenSource();
            _confirmationId = Guid.NewGuid().ToString();
            _invalidated = false;
            _started = false;
            _submitted = false;
            _target = null;
            _result = null;
        }

        StartWork("load", null);
    }

    private void StartWork(string? action, string? method, string? confirmation = null)
    {
        bool start;
        lock (_lock)
        {
            start = !_disposed && !_invalidated && !_busy && ReferenceEquals(_auth.CurrentAccount, _account)
                && (action == "load" || confirmation == _confirmationId)
                && (action == "load" ? !_started
                    : action == "confirm" ? _target is not null && !_submitted && method is not null && _target.Methods.Contains(method)
                    : action == "status" && _result?.Uuid is not null);
            if (!start) return;
            _started = true;
            _busy = true;
            if (action == "confirm") _submitted = true;
            _form = new MergeForm(this, Card(
                action == "load" ? "Loading merge confirmation..." : "Checking with GitHub. Acceptance does not mean merged.",
                null, cancel: true));
            _currentWork = Task.Run(() => WorkAsync(action!, method));
        }

        RaiseItemsChanged();
    }

    private async Task WorkAsync(string action, string? method)
    {
        try
        {
            _cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_auth.CurrentAccount, _account)) return;
            if (action == "load")
            {
                var target = await _client.GetTargetAsync(_account, _repository, _number, _cancellation.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_invalidated) return;
                    _target = target;
                    _form = new MergeForm(this, Confirmation(target));
                }
            }
            else
            {
                PullRequestMergeTarget target;
                string? uuid;
                lock (_lock)
                {
                    target = _target!;
                    uuid = _result?.Uuid;
                }

                var result = action == "confirm"
                    ? await _client.MergeAsync(_account, target, method!, _cancellation.Token).ConfigureAwait(false)
                    : await _client.GetStatusAsync(_account, target, uuid!, _cancellation.Token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_invalidated) return;
                    _result = result;
                    _form = new MergeForm(this, Card(result.Summary, result.Uuid is not null ? "status" : result.Status == "failed" ? "prepare" : null, cancel: result.Uuid is not null));
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (_invalidated) return;
                var warning = _submitted ? "No completion is confirmed. Check GitHub before submitting another merge. " : string.Empty;
                _form = new MergeForm(this, Card(warning + ex.Message, _result?.Uuid is null ? "prepare" : "status"));
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                _busy = false;
                publish = !_invalidated;
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _auth.AccountChanged -= OnAccountChanged;
        Invalidate("This confirmation is no longer active. Check GitHub for any submitted request.");
        _ = CurrentWork.ContinueWith(_ => _cancellation.Dispose(), TaskScheduler.Default);
    }

    private void OnAccountChanged(object? sender, EventArgs e) =>
        Invalidate("The account changed. Reopen the PR using the current account. Any submitted merge continues on GitHub.");

    private void Invalidate(string message, bool allowFresh = false, string? expectedConfirmation = null)
    {
        lock (_lock)
        {
            if (_invalidated || (expectedConfirmation is not null && expectedConfirmation != _confirmationId)) return;
            _invalidated = true;
            _target = null;
            _result = null;
            _form = new MergeForm(this, Card(message, allowFresh ? "prepare" : null, includeLink: allowFresh));
        }

        _cancellation.Cancel();
        IsLoading = false;
        RaiseItemsChanged();
    }

    private string Confirmation(PullRequestMergeTarget target)
    {
        var choices = string.Join(',', target.Methods.Select(method =>
            $$"""{"title":{{JsonSerializer.Serialize(method)}},"value":{{JsonSerializer.Serialize(method)}}}"""));
        var scope = target.StackScope is null
            ? "No stack is currently reported."
            : $"Current stack metadata: {target.StackScope}.";
        return Card(
            $"Confirm {_repository}#{_number} as {_account.Login}@{_account.Host.Name}. Current target branch: {target.BaseRef}. Expected head SHA: {target.HeadSha}. {scope} Scope: this PR and ALL open downstack PRs if it is stacked. GitHub will use the branch merge queue if configured; the queue controls its merge method. Otherwise use the selected direct-merge method. Repository rules are enforced, never bypassed. The API pins only this PR's head SHA, not the target branch or downstack scope. These may change after our final check. If you require a fixed branch or exact downstack PR set, do not confirm; review on GitHub instead.",
            "confirm", cancel: true,
            input: $$"""{"type":"Input.ChoiceSet","id":"method","label":"Direct-merge method","style":"compact","isRequired":true,"value":{{JsonSerializer.Serialize(target.Methods[0])}},"choices":[{{choices}}]},{"type":"Input.Toggle","id":"scopeAccepted","title":"I authorize the current target and automatic downstack scope, including concurrent changes after the final check.","valueOn":"true","valueOff":"false","value":"false","isRequired":true,"errorMessage":"Review and accept the async API scope before confirming."}""");
    }

    private string Card(string text, string? action, bool cancel = false, string? input = null, bool includeLink = true)
    {
        var elements = new List<string>
        {
            $$"""{"type":"TextBlock","text":{{JsonSerializer.Serialize(text)}},"wrap":true}""",
        };
        if (input is not null) elements.Add(input);
        var actions = new List<string>();
        if (action is not null)
        {
            var title = action switch
            {
                "confirm" => "Confirm merge or enqueue",
                "prepare" => "Load fresh confirmation",
                _ => "Check status",
            };
            actions.Add($$$"""{"type":"Action.Submit","title":"{{{title}}}","data":{"action":"{{{action}}}","confirmation":"{{{_confirmationId}}}"}}""");
        }

        if (cancel)
        {
            actions.Add($$$"""{"type":"Action.Submit","title":"Cancel / stop checking","associatedInputs":"none","data":{"action":"cancel","confirmation":"{{{_confirmationId}}}"}}""");
        }

        if (includeLink)
        {
            actions.Add($$"""{"type":"Action.OpenUrl","title":"Open PR on GitHub","url":{{JsonSerializer.Serialize(_webUrl.AbsoluteUri)}}}""");
        }

        return $$"""{"type":"AdaptiveCard","version":"1.6","body":[{{string.Join(',', elements)}}],"actions":[{{string.Join(',', actions)}}]}""";
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed partial class MergeForm : FormContent
    {
        private readonly MergePullRequestPage _page;

        public MergeForm(MergePullRequestPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.HandleSubmit(inputs, data);
    }
}
