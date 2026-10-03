// Copyright (c) BaldBeardedBuilder LLC
// BaldBeardedBuilder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class PullRequestActionsPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IPullRequestActionsClient _client;
    private readonly GitHubAccount _account;
    private readonly string _repository;
    private readonly int _number;
    private readonly Uri _webUrl;
    private readonly MutationExecutor _mutations;
    private readonly Func<bool>? _isCurrent;
    private readonly Func<Task>? _refresh;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private RepositoryPullRequestsPage? _owner;
    private PullRequestActionSnapshot? _snapshot;
    private PullRequestActionOptions? _options;
    private PullRequestActionsForm _form;
    private Task _currentWork = Task.CompletedTask;
    private string _confirmation = Guid.NewGuid().ToString();
    private bool _busy;
    private bool _invalidated;
    private bool _disposed;

    internal RepositoryPullRequestsPage? Owner
    {
        get => _owner;
        init => _owner = value;
    }

    internal Task CurrentWork
    {
        get { lock (_lock) return _currentWork; }
    }

    public PullRequestActionsPage(
        AuthService auth,
        IPullRequestActionsClient client,
        GitHubAccount account,
        string repository,
        int number,
        Uri webUrl,
        Func<bool>? isCurrent = null,
        Func<Task>? refresh = null)
    {
        _auth = auth;
        _client = client;
        _account = account;
        _repository = repository;
        _number = number;
        _webUrl = webUrl;
        _isCurrent = isCurrent;
        _refresh = refresh;
        _mutations = new MutationExecutor(auth);
        _form = new PullRequestActionsForm(this, Card("Loading current pull request and available choices..."));
        Id = $"com.baldbeardedbuilder.cmdpal.github.pull-request-actions.{Guid.NewGuid():N}";
        Name = "Manage pull request";
        Title = $"Manage {_repository}#{_number}";
        Icon = Icons.PullRequests;
        _accountSubscription = auth.Subscribe(this, static page => page.OnAccountChanged());
    }

    public override IContent[] GetContent()
    {
        StartLoad();
        lock (_lock) return [_form];
    }

    internal ICommandResult HandleSubmit(string inputs, string data)
    {
        var action = ReadString(data, "action");
        var confirmation = ReadString(data, "confirmation");
        if (action == "cancel" && confirmation == _confirmation)
        {
            lock (_lock)
            {
                if (_busy || _invalidated) return CommandResult.KeepOpen();
                _confirmation = Guid.NewGuid().ToString();
                _preparedAction = null;
                _preparedValue = null;
                _form = new PullRequestActionsForm(this, FormCard(null));
            }

            RaiseItemsChanged();
            return CommandResult.KeepOpen();
        }

        if (action == "confirm" && confirmation == _confirmation)
        {
            StartMutation();
            return CommandResult.KeepOpen();
        }

        if (action is "close" or "reopen" or "review-add" or "review-remove" or "assignee-add" or "assignee-remove" or "label-add" or "label-remove")
        {
            PrepareConfirmation(action, inputs);
        }

        return CommandResult.KeepOpen();
    }

    private void StartLoad()
    {
        lock (_lock)
        {
            if (!IsCurrent() || _busy || _snapshot is not null) return;
            _busy = true;
            _currentWork = LoadAsync(_lifetime.Token);
        }

        IsLoading = true;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _client.GetSnapshotAsync(_account, _repository, _number, cancellationToken).ConfigureAwait(false);
            var options = await _client.GetOptionsAsync(_account, _repository, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                if (!IsCurrent()) return;
                _snapshot = snapshot;
                _options = options;
                _form = new PullRequestActionsForm(this, FormCard(null));
            }
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            lock (_lock)
            {
                if (_disposed || _invalidated) return;
                _form = new PullRequestActionsForm(this, Card(ex is GitHubApiException api ? api.Message : "Couldn't load pull request choices."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_lock) _busy = false;
            IsLoading = false;
            RaiseItemsChanged();
        }
    }

    private void PrepareConfirmation(string action, string inputs)
    {
        PullRequestActionSnapshot? snapshot;
        PullRequestActionOptions? options;
        lock (_lock)
        {
            if (_disposed || _invalidated || _busy || _snapshot is null || _options is null) return;
            snapshot = _snapshot;
            options = _options;
        }

        var selected = action switch
        {
            "review-add" or "review-remove" => ReadString(inputs, "reviewer"),
            "assignee-add" or "assignee-remove" => ReadString(inputs, "assignee"),
            "label-add" or "label-remove" => ReadString(inputs, "label"),
            _ => null,
        };
        var team = false;
        if (action.StartsWith("review-", StringComparison.Ordinal))
        {
            if (selected is null || !selected.StartsWith("team:", StringComparison.Ordinal) && !selected.StartsWith("user:", StringComparison.Ordinal)) return;
            team = selected.StartsWith("team:", StringComparison.Ordinal);
            selected = selected[(selected.IndexOf(':') + 1)..];
            var candidates = team ? options.Teams : options.Reviewers;
            var currentlyRequested = (team ? snapshot.RequestedTeams : snapshot.RequestedReviewers)
                .Contains(selected, StringComparer.OrdinalIgnoreCase);
            if (action == "review-add" ? !candidates.Contains(selected, StringComparer.OrdinalIgnoreCase) : !currentlyRequested) return;
        }
        else if (action.StartsWith("assignee-", StringComparison.Ordinal)
            && (selected is null || (action == "assignee-add"
                ? !options.Assignees.Contains(selected, StringComparer.OrdinalIgnoreCase)
                : !snapshot.Assignees.Contains(selected, StringComparer.OrdinalIgnoreCase))))
        {
            return;
        }
        else if (action.StartsWith("label-", StringComparison.Ordinal)
            && (selected is null || (action == "label-add"
                ? !options.Labels.Contains(selected, StringComparer.OrdinalIgnoreCase)
                : !snapshot.Labels.Contains(selected, StringComparer.OrdinalIgnoreCase))))
        {
            return;
        }

        try
        {
            EnsureAllowed(action, snapshot, selected, team);
        }
        catch (GitHubApiException ex)
        {
            lock (_lock) _form = new PullRequestActionsForm(this, Card(ex.Message));
            RaiseItemsChanged();
            return;
        }

        lock (_lock)
        {
            if (_disposed || _invalidated || _busy) return;
            _confirmation = Guid.NewGuid().ToString();
            var title = TitleFor(action);
            var template = MutationConfirmation.Card(_account, title, $"{_repository}#{_number}", ConfirmationText(action, selected, team))
                .Replace(
                    """ "data": { "action": "confirm" } """.Trim(),
                    $$""" "data": { "action": "confirm", "confirmation": "{{_confirmation}}" } """.Trim(),
                    StringComparison.Ordinal);
            _form = new PullRequestActionsForm(this,
                template);
        }

        RaiseItemsChanged();
        _preparedAction = action;
        _preparedValue = selected;
        _preparedTeam = team;
    }

    private string? _preparedAction;
    private string? _preparedValue;
    private bool _preparedTeam;

    private void StartMutation()
    {
        string action;
        string? value;
        bool team;
        lock (_lock)
        {
            if (!IsCurrent() || _busy || _preparedAction is null) return;
            action = _preparedAction;
            value = _preparedValue;
            team = _preparedTeam;
            _busy = true;
            _form = new PullRequestActionsForm(this, Card("Checking the current pull request and submitting the confirmed action..."));
            _currentWork = MutateAsync(action, value, team);
        }

        IsLoading = true;
        RaiseItemsChanged();
    }

    private async Task MutateAsync(string action, string? value, bool team)
    {
        try
        {
            var target = $"{_repository}#{_number}/{action}/{value}";
            var result = await _mutations.ExecuteAsync(
                _account,
                target,
                async token =>
                {
                    if (!IsCurrent())
                    {
                        throw new GitHubApiException("This notification is no longer current. Refresh before trying again.");
                    }

                    var current = await _client.GetSnapshotAsync(_account, _repository, _number, token).ConfigureAwait(false);
                    if (!IsCurrent())
                    {
                        throw new GitHubApiException("This notification changed. Refresh and review the action again.");
                    }

                    EnsureAllowed(action, current, value, team);
                    return true;
                },
                async token =>
                {
                    await SubmitAsync(action, value, team, token).ConfigureAwait(false);
                    return new MutationResult<string>(MutationState.Completed, Value: "The change was confirmed by GitHub.");
                }).ConfigureAwait(false);

            if (result.State == MutationState.Completed)
            {
                _mutations.ObserveCompletion(_account, target);
                if (_refresh is not null)
                {
                    await _refresh().ConfigureAwait(false);
                }
                else if (_owner is not null)
                {
                    await _owner.RefreshAsync().ConfigureAwait(false);
                }
            }

            lock (_lock)
            {
                if (_disposed || _invalidated) return;
                _confirmation = Guid.NewGuid().ToString();
                _preparedAction = null;
                _preparedValue = null;
                _form = new PullRequestActionsForm(this, Card(result.State == MutationState.Completed
                    ? result.Value ?? "The change was confirmed by GitHub."
                    : result.Error ?? "The change was not completed. Refresh GitHub before retrying."));
            }
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            lock (_lock)
            {
                if (_disposed || _invalidated) return;
                _form = new PullRequestActionsForm(this, Card(ex is GitHubApiException api ? api.Message : "The request outcome could not be confirmed. Refresh GitHub before retrying."));
            }
        }
        finally
        {
            lock (_lock) _busy = false;
            IsLoading = false;
            RaiseItemsChanged();
        }
    }

    private Task SubmitAsync(string action, string? value, bool team, CancellationToken token) => action switch
    {
        "close" => _client.SetStateAsync(_account, _repository, _number, open: false, token),
        "reopen" => _client.SetStateAsync(_account, _repository, _number, open: true, token),
        "review-add" or "review-remove" => _client.SetReviewerAsync(_account, _repository, _number, value!, team, action == "review-add", token),
        "assignee-add" or "assignee-remove" => _client.SetAssigneeAsync(_account, _repository, _number, value!, action == "assignee-add", token),
        "label-add" or "label-remove" => _client.SetLabelAsync(_account, _repository, _number, value!, action == "label-add", token),
        _ => throw new GitHubApiException("Choose a supported pull request action."),
    };

    private bool IsCurrent() =>
        !_disposed && !_invalidated && ReferenceEquals(_auth.CurrentAccount, _account) && (_isCurrent?.Invoke() ?? true);

    private static void EnsureAllowed(string action, PullRequestActionSnapshot snapshot, string? value, bool team)
    {
        if (action is "close" or "reopen")
        {
            if (!snapshot.CanWrite) throw new GitHubApiException("Write access to this repository is required to change pull request state.");
            if (snapshot.Merged) throw new GitHubApiException("Merged pull requests cannot be reopened or closed.");
            if (action == "close" && snapshot.State != SubjectState.Open) throw new GitHubApiException("Only open pull requests can be closed.");
            if (action == "reopen" && snapshot.State != SubjectState.Closed) throw new GitHubApiException("Only closed, unmerged pull requests can be reopened.");
            return;
        }

        if (action.StartsWith("review-", StringComparison.Ordinal))
        {
            if (!snapshot.CanWrite) throw new GitHubApiException("Write access to this repository is required to manage review requests.");
            if (snapshot.Merged || snapshot.State != SubjectState.Open) throw new GitHubApiException("Only open, unmerged pull requests can change review requests.");
            var current = team ? snapshot.RequestedTeams : snapshot.RequestedReviewers;
            if (current.Contains(value!, StringComparer.OrdinalIgnoreCase) == (action == "review-add"))
            {
                throw new GitHubApiException("The requested reviewer state changed. Refresh and review the action again.");
            }

            return;
        }

        if (action.StartsWith("assignee-", StringComparison.Ordinal))
        {
            if (!snapshot.CanTriage) throw new GitHubApiException("Triage or write access is required to edit pull request assignees.");
            if (snapshot.Assignees.Contains(value!, StringComparer.OrdinalIgnoreCase) == (action == "assignee-add"))
                throw new GitHubApiException("The requested assignee state changed. Refresh and review the action again.");
            return;
        }

        if (action.StartsWith("label-", StringComparison.Ordinal))
        {
            if (!snapshot.CanTriage) throw new GitHubApiException("Triage or write access is required to edit pull request labels.");
            if (snapshot.Labels.Contains(value!, StringComparer.OrdinalIgnoreCase) == (action == "label-add"))
                throw new GitHubApiException("The requested label state changed. Refresh and review the action again.");
        }
    }

    private string FormCard(string? message)
    {
        PullRequestActionSnapshot? snapshot;
        PullRequestActionOptions? options;
        lock (_lock)
        {
            snapshot = _snapshot;
            options = _options;
        }

        if (snapshot is null || options is null) return Card(message ?? "Loading pull request choices...");
        var lines = new List<string>
        {
            $"Current state: {snapshot.State}{(snapshot.Merged ? " (merged)" : string.Empty)}.",
            $"Review requests: {Join(snapshot.RequestedReviewers.Select(name => $"@{name}").Concat(snapshot.RequestedTeams.Select(slug => $"team:{slug}")))}.",
            $"Assignees: {Join(snapshot.Assignees.Select(name => $"@{name}"))}.",
            $"Labels: {Join(snapshot.Labels)}.",
        };
        var body = new List<string>
        {
            $$"""{"type":"TextBlock","text":{{GitHubJson.String(string.Join("\n", lines))}},"wrap":true}""",
        };
        if (message is not null) body.Add($$"""{"type":"TextBlock","text":{{GitHubJson.String(message)}},"wrap":true,"color":"Attention"}""");

        var inputs = new List<string>();
        var actions = new List<string>();
        if (snapshot.CanWrite && snapshot.State == SubjectState.Open)
            actions.Add(Action("close", "Close pull request"));
        if (snapshot.CanWrite && snapshot.State == SubjectState.Closed && !snapshot.Merged)
            actions.Add(Action("reopen", "Reopen pull request"));
        if (snapshot.CanWrite && snapshot.State == SubjectState.Open && !snapshot.Merged)
        {
            var reviewers = options.Reviewers.Select(login => (Value: "user:" + login, Title: "@" + login))
                .Concat(options.Teams.Select(slug => (Value: "team:" + slug, Title: "Team " + slug)));
            reviewers = reviewers.Concat(snapshot.RequestedReviewers.Select(login => (Value: "user:" + login, Title: "@" + login)))
                .Concat(snapshot.RequestedTeams.Select(slug => (Value: "team:" + slug, Title: "Team " + slug)))
                .DistinctBy(choice => choice.Value, StringComparer.OrdinalIgnoreCase);
            inputs.Add(Choice("reviewer", "Reviewer", reviewers));
            actions.Add(Action("review-add", "Request reviewer"));
            actions.Add(Action("review-remove", "Remove review request"));
        }

        if (snapshot.CanTriage)
        {
            inputs.Add(Choice("assignee", "Assignee", options.Assignees.Concat(snapshot.Assignees)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(login => (login, "@" + login))));
            actions.Add(Action("assignee-add", "Assign"));
            actions.Add(Action("assignee-remove", "Unassign"));
            inputs.Add(Choice("label", "Label", options.Labels.Concat(snapshot.Labels)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(label => (label, label))));
            actions.Add(Action("label-add", "Add label"));
            actions.Add(Action("label-remove", "Remove label"));
        }

        actions.Add($$"""{"type":"Action.OpenUrl","title":"Open on GitHub","url":{{GitHubJson.String(_webUrl.AbsoluteUri)}}}""");
        body.AddRange(inputs);
        return $$"""{"type":"AdaptiveCard","version":"1.6","body":[{{string.Join(',', body)}}],"actions":[{{string.Join(',', actions)}}]}""";
    }

    private static string TitleFor(string action) => action switch
    {
        "close" => "Close pull request",
        "reopen" => "Reopen pull request",
        "review-add" => "Request pull request review",
        "review-remove" => "Remove pull request review request",
        "assignee-add" => "Assign pull request",
        "assignee-remove" => "Unassign pull request",
        "label-add" => "Add pull request label",
        _ => "Remove pull request label",
    };

    private static string ConfirmationText(string action, string? value, bool team) => action switch
    {
        "close" => "Close this pull request without merging it.",
        "reopen" => "Reopen this closed, unmerged pull request.",
        "review-add" => $"Request review from {(team ? "team " : "@")}{value}.",
        "review-remove" => $"Remove the review request from {(team ? "team " : "@")}{value}.",
        "assignee-add" => $"Add @{value} as an assignee without changing existing assignees.",
        "assignee-remove" => $"Remove @{value} as an assignee without changing other assignees.",
        "label-add" => $"Add the {value} label without changing existing labels.",
        _ => $"Remove the {value} label without changing other labels.",
    };

    private static string Choice(string id, string label, IEnumerable<(string Value, string Title)> choices)
        => JsonSerializer.Serialize(new
        {
            type = "Input.ChoiceSet",
            id,
            label,
            style = "compact",
            choices = choices.Select(choice => new { title = choice.Title, value = choice.Value }),
        });

    private static string Action(string action, string title) =>
        JsonSerializer.Serialize(new { type = "Action.Submit", title, data = new { action } });

    private string Card(string text) => JsonSerializer.Serialize(new
    {
        type = "AdaptiveCard",
        version = "1.6",
        body = new[] { new { type = "TextBlock", text, wrap = true } },
        actions = new[] { new { type = "Action.OpenUrl", title = "Open on GitHub", url = _webUrl.AbsoluteUri } },
    });

    private static string Join(IEnumerable<string> values)
    {
        var result = string.Join(", ", values);
        return result.Length == 0 ? "none" : result;
    }

    private static string? ReadString(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _accountSubscription.Dispose();
        _mutations.Dispose();
        Invalidate("This pull request action page is no longer active.");
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void OnAccountChanged() => Invalidate("The account changed. Reopen this pull request using the current account.");

    private void Invalidate(string message)
    {
        lock (_lock)
        {
            if (_invalidated) return;
            _invalidated = true;
            _lifetime.Cancel();
            _snapshot = null;
            _options = null;
            _preparedAction = null;
            _form = new PullRequestActionsForm(this, Card(message));
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    private sealed partial class PullRequestActionsForm : FormContent
    {
        private readonly PullRequestActionsPage _page;

        public PullRequestActionsForm(PullRequestActionsPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.HandleSubmit(inputs, data);
    }
}
