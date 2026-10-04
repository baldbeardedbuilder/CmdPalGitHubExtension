// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class IssueDetailsPage
{
    private readonly IIssueMutationsClient? _mutationClient;
    private readonly IssueMutationSession? _mutations;
    private GitHubAccount? _loadedAccount;
    private long _targetRevision;
    private Review? _review;
    private (IssueChangeKind Kind, bool Add, IReadOnlyList<string> Names)? _choices;
    private (GitHubAccount Account, Uri ApiUrl, string Repository)? _initialIssue;
    private Action<GitHubIssue>? _changed;

    private sealed record Review(GitHubAccount Account, string Repository, GitHubIssue Issue, IssueChange Change, long Revision);

    internal Task CurrentMutation => CurrentLoad;

    private void ActivateIssue()
    {
        (GitHubAccount Account, Uri ApiUrl, string Repository)? initial;
        lock (_lock)
        {
            initial = _initialIssue;
            _initialIssue = null;
        }

        if (initial is { } pending)
        {
            LoadIssue(pending.Account, pending.ApiUrl, pending.Repository);
        }
    }

    private bool IsCurrent(Review review)
    {
        lock (_lock)
        {
            return !_load.Disposed && _targetRevision == review.Revision
                && ReferenceEquals(_loadedAccount, review.Account) && ReferenceEquals(_auth.CurrentAccount, review.Account);
        }
    }

    private ICommandResult Submit(IssueDetailsForm source, string inputs, string action)
    {
        Review? confirmed = null;
        (GitHubAccount Account, string Repository, GitHubIssue Issue, long Revision)? target = null;
        lock (_lock)
        {
            if (_load.Disposed || _load.Fetching || !ReferenceEquals(source, _form))
            {
                return CommandResult.KeepOpen();
            }

            if (action == IssueDetailsActions.Confirm)
            {
                confirmed = _review;
                _review = null;
                if (confirmed is not null)
                {
                    _form = new IssueDetailsForm(this, IssueDetailsCards.Loading());
                }
            }
            else if (_loadedAccount is { } account && ReferenceEquals(account, _auth.CurrentAccount)
                && _repository is { } repository && _issue is { } issue)
            {
                target = (account, repository, issue, _targetRevision);
            }
        }

        if (confirmed is not null)
        {
            StartMutation(confirmed);
            return CommandResult.KeepOpen();
        }

        if (action is IssueDetailsActions.OpenInBrowser or IssueDetailsActions.Retry)
        {
            return HandleSubmit(action);
        }

        if (target is not { } captured || _mutations is null)
        {
            return CommandResult.KeepOpen();
        }

        if (action == "cancel")
        {
            ShowDetails(captured.Revision);
            return CommandResult.KeepOpen();
        }

        IssueChange? change = action switch
        {
            IssueDetailsActions.CloseCompleted => new(IssueChangeKind.State, State: SubjectState.Closed),
            IssueDetailsActions.CloseNotPlanned => new(IssueChangeKind.State, State: SubjectState.NotPlanned),
            IssueDetailsActions.Reopen => new(IssueChangeKind.State, State: SubjectState.Open),
            IssueDetailsActions.AssignSelf => new(IssueChangeKind.Assignee, captured.Account.Login),
            IssueDetailsActions.RemoveSelf => new(IssueChangeKind.Assignee, captured.Account.Login, Add: false),
            _ => null,
        };

        if (action == IssueDetailsActions.Select)
        {
            lock (_lock)
            {
                if (_choices is { } choices && TryReadSelection(inputs, choices.Names.Count, out var index))
                {
                    change = new(choices.Kind, choices.Names[index], choices.Add);
                }
            }

            if (change is null)
            {
                ShowDetails(captured.Revision, "Choose an item from the current picker. No request was sent.");
            }
        }
        else if (action is IssueDetailsActions.AddAssignee or IssueDetailsActions.RemoveAssignee
            or IssueDetailsActions.AddLabel or IssueDetailsActions.RemoveLabel)
        {
            StartPicker(captured.Account, captured.Repository, captured.Issue, captured.Revision,
                action is IssueDetailsActions.AddAssignee or IssueDetailsActions.RemoveAssignee ? IssueChangeKind.Assignee : IssueChangeKind.Label,
                action is IssueDetailsActions.AddAssignee or IssueDetailsActions.AddLabel);
        }

        if (change is not null)
        {
            if (!change.Allowed(captured.Issue))
            {
                ShowDetails(captured.Revision, "This action no longer applies. Refresh the issue before trying again.");
            }
            else
            {
                var review = new Review(captured.Account, captured.Repository, captured.Issue, change, captured.Revision);
                lock (_lock)
                {
                    if (!IsCurrent(review) || !ReferenceEquals(source, _form))
                    {
                        return CommandResult.KeepOpen();
                    }

                    _review = review;
                    _choices = null;
                    _form = new IssueDetailsForm(this, MutationConfirmation.Card(
                        review.Account, change.Title, $"{review.Repository}#{review.Issue.Number}",
                        change.Kind == IssueChangeKind.State
                            ? "This changes the issue's state. Notification Done is a separate action."
                            : "Only this selection changes. Other assignees and labels stay in place.",
                        IssueDetailsActions.Confirm));
                }

                PublishItemsChanged();
            }
        }

        return CommandResult.KeepOpen();
    }

    private void StartPicker(GitHubAccount account, string repository, GitHubIssue issue, long revision, IssueChangeKind kind, bool add)
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_targetRevision != revision || !ReferenceEquals(account, _auth.CurrentAccount) || !_load.TryBegin(true, out operation))
            {
                return;
            }

            _review = null;
            _choices = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.Loading());
        }

        _load.Publish(operation, () => IsLoading = true);
        _load.Publish(operation, PublishItemsChanged);
        _load.Run(operation, async () =>
        {
            var names = add
                ? await _mutations!.ChoicesAsync(account, repository, kind, operation.Token).ConfigureAwait(false)
                : kind == IssueChangeKind.Assignee ? issue.Assignees : issue.Labels;
            names = names.Where(name => new IssueChange(kind, name).Contains(issue) != add).ToArray();
            lock (_lock)
            {
                if (!_load.IsCurrent(operation))
                {
                    return;
                }

                _choices = (kind, add, names);
                _form = new IssueDetailsForm(this, IssueDetailsCards.Picker(kind, add, names));
                _load.Succeed(operation, null);
            }
        }, () => FinishOperation(operation, revision), "GitHub took too long to load the picker. Refresh before choosing again.",
            area: DiagnosticArea.Issues);
    }

    private void StartMutation(Review review)
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_mutations is null || !IsCurrent(review) || !_load.TryBegin(true, out operation))
            {
                return;
            }

            _form = new IssueDetailsForm(this, IssueDetailsCards.Loading());
        }

        _load.Publish(operation, () => IsLoading = true);
        _load.Publish(operation, PublishItemsChanged);
        _load.Run(operation, async () =>
        {
            var result = await _mutations.ExecuteAsync(review.Account, review.Repository, review.Issue, review.Change,
                () => IsCurrent(review), operation.Token).ConfigureAwait(false);
            Action<GitHubIssue>? changed = null;
            lock (_lock)
            {
                if (!_load.IsCurrent(operation) || !IsCurrent(review) || result.State == MutationState.Stale)
                {
                    return;
                }

                if (result.State == MutationState.Completed && result.Value is { } updated)
                {
                    _issue = updated;
                    changed = _changed;
                }

                _form = new IssueDetailsForm(this, IssueDetailsCards.Details(review.Repository, _issue!,
                    result.State == MutationState.Completed ? "Issue updated." : result.Error ?? "The change is still unconfirmed. Refresh to check GitHub.",
                    result.AuthorizeUrl), showsDescription: true);
                _load.Succeed(operation, null);
            }

            if (changed is not null && IsCurrent(review))
            {
                changed(result.Value!);
            }
        }, () => FinishOperation(operation, review.Revision),
            "GitHub took too long to respond. Refresh to check whether the issue changed.",
            area: DiagnosticArea.Issues, mutation: true, diagnose: false);
    }

    private void FinishOperation(ListLoadState.Operation operation, long revision)
    {
        string? error;
        Uri? authorizeUrl;
        lock (_lock)
        {
            error = _load.Error;
            authorizeUrl = _load.AuthorizeUrl;
        }

        if (error is not null)
        {
            ShowDetails(revision, error, authorizeUrl);
        }

        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, PublishItemsChanged);
    }

    private void ShowDetails(long revision, string? feedback = null, Uri? authorizeUrl = null)
    {
        lock (_lock)
        {
            if (_load.Disposed || _targetRevision != revision || _issue is null || _repository is null)
            {
                return;
            }

            _review = null;
            _choices = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.Details(_repository, _issue,
                feedback, authorizeUrl), showsDescription: true);
        }

        PublishItemsChanged();
    }

    private static bool TryReadSelection(string inputs, int count, out int index)
    {
        index = -1;
        try
        {
            using var json = JsonDocument.Parse(inputs);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("selection", out var selection) && selection.ValueKind == JsonValueKind.String
                && int.TryParse(selection.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out index)
                && index >= 0 && index < count;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
