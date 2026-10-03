// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class PullRequestDetailsPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IPullRequestFeatureClient _client;
    private readonly string _repository;
    private readonly int _number;
    private readonly Func<string, int, string?, ICommand?>? _contextualCodespaceFactory;
    private readonly Func<bool>? _isCurrent;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly IDisposable _accountSubscription;
    private GitHubAccount? _account;
    private PullRequestDetailsSnapshot? _details;
    private ICommand? _contextualCodespaceCommand;
    private PullRequestReviewDraft? _reviewDraft;
    private string? _reviewDraftHeadSha;
    private PendingPullRequestReview? _pendingReview;
    private bool _pendingReviewCreationUnknown;
    private string? _pendingConfirmation;
    private string? _preparedAction;
    private PullRequestDetailsForm _form;
    private bool _busy;
    private bool _disposed;
    private bool _outcomeUnknown;
    private string? _feedback;

    internal RepositoryPullRequestsPage? Owner { get; init; }
    internal Task CurrentWork { get; private set; } = Task.CompletedTask;

    internal PullRequestDetailsPage(
        AuthService auth,
        IPullRequestFeatureClient client,
        GitHubAccount account,
        string repository,
        int number,
        Func<string, int, string?, ICommand?>? contextualCodespaceFactory = null,
        Func<bool>? isCurrent = null)
    {
        _isCurrent = isCurrent;
        _auth = auth;
        _client = client;
        _account = account;
        _repository = repository;
        _number = number;
        _contextualCodespaceFactory = contextualCodespaceFactory;
        Id = $"com.baldbeardedbuilder.cmdpal.github.pull-request-details.{Uri.EscapeDataString(repository)}.{number}.{Guid.NewGuid():N}";
        Name = "Pull request details";
        Title = $"Pull request {repository}#{number}";
        Icon = Icons.PullRequests;
        _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.Loading(repository, number));
        _accountSubscription = auth.Subscribe(this, static page => page.AccountChanged());
    }

    internal ICommand? ContextualCodespaceCommand
    {
        get
        {
            GitHubAccount? account;
            PullRequestDetailsSnapshot? details;
            lock (_lock)
            {
                if (_disposed || _contextualCodespaceCommand is not null)
                    return _contextualCodespaceCommand;
                account = _account;
                details = _details;
            }

            if (account is null || details is null || _contextualCodespaceFactory is null || !IsLive(account))
                return null;
            var command = _contextualCodespaceFactory(_repository, _number, details.PullRequest.HeadRef);
            lock (_lock)
            {
                if (_disposed || !ReferenceEquals(account, _account) || !ReferenceEquals(details, _details) || !IsLive(account))
                {
                    (command as IDisposable)?.Dispose();
                    return null;
                }

                _contextualCodespaceCommand ??= command;
                if (!ReferenceEquals(_contextualCodespaceCommand, command))
                    (command as IDisposable)?.Dispose();
                return _contextualCodespaceCommand;
            }
        }
    }

    public override IContent[] GetContent()
    {
        StartLoad();
        lock (_lock) return [_form];
    }

    public void Dispose()
    {
        _accountSubscription.Dispose();
        ICommand? contextualCodespace;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _load.Dispose();
            _account = null;
            _details = null;
            contextualCodespace = _contextualCodespaceCommand;
            _contextualCodespaceCommand = null;
            _reviewDraft = null;
            _reviewDraftHeadSha = null;
            _pendingReview = null;
            _pendingReviewCreationUnknown = false;
            _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.SignedOut());
        }
        (contextualCodespace as IDisposable)?.Dispose();
        IsLoading = false;
    }

    private CommandResult Submit(PullRequestDetailsForm source, string inputs, string data, string? confirmation)
    {
        var action = ReadString(data, "action");
        ListLoadState.Operation? operation = null;
        string? mutation = null;
        lock (_lock)
        {
            if (_disposed || _busy || !ReferenceEquals(source, _form)
                || _details is null || _account is null || !IsLive(_account))
                return CommandResult.KeepOpen();
            if (action == "cancel" && confirmation == _pendingConfirmation)
            {
                _pendingConfirmation = null;
                _preparedAction = null;
                if (_pendingReview is null && !_pendingReviewCreationUnknown)
                {
                    _reviewDraft = null;
                    _reviewDraftHeadSha = null;
                }
                _form = DetailsForm(null);
            }
            else if (action == "refresh")
            {
                _details = null;
                _feedback = null;
                _outcomeUnknown = _pendingReviewCreationUnknown;
                StartLoadUnderLock(out operation);
            }
            else if (action is "draft" or "ready" or "update-branch")
            {
                if (_outcomeUnknown) return CommandResult.KeepOpen();
                _preparedAction = action;
                _pendingConfirmation = Guid.NewGuid().ToString("N");
                _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.Confirm(
                    _repository, _number, ConfirmationTitle(action), _details.HeadSha, _pendingConfirmation,
                    _account.Login, _account.Host.Name));
            }
            else if (action == "prepare-review")
            {
                var review = ReadReviewDraft(inputs);
                if (review is null)
                {
                    _feedback = "Choose a review action and enter valid review text.";
                    _form = DetailsForm(_feedback);
                }
                else if (_pendingReview is { } pending)
                {
                    var pendingDraft = review with { Body = pending.Body };
                    _reviewDraft = pendingDraft;
                    _reviewDraftHeadSha = pending.HeadSha;
                    _preparedAction = "review";
                    _pendingConfirmation = Guid.NewGuid().ToString("N");
                    _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.ConfirmReview(
                        _repository, _number, pendingDraft, pending.HeadSha, _pendingConfirmation,
                        _account.Login, _account.Host.Name));
                }
                else if (review.Event == "REQUEST_CHANGES" && string.IsNullOrWhiteSpace(review.Body))
                {
                    _reviewDraft = review;
                    _reviewDraftHeadSha = _details.HeadSha;
                    _feedback = "Add a clear reason when requesting changes.";
                    _form = DetailsForm(_feedback);
                }
                else if (_outcomeUnknown)
                {
                    _reviewDraft = review;
                    _reviewDraftHeadSha = _details.HeadSha;
                    _feedback = "A previous review request may have reached GitHub. Check the reviews before trying again.";
                    _form = DetailsForm(_feedback);
                }
                else
                {
                    _reviewDraft = review;
                    _reviewDraftHeadSha = _details.HeadSha;
                    _preparedAction = "review";
                    _pendingConfirmation = Guid.NewGuid().ToString("N");
                    _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.ConfirmReview(
                        _repository, _number, review, _details.HeadSha, _pendingConfirmation,
                        _account.Login, _account.Host.Name));
                }
            }
            else if (action == "confirm" && confirmation == _pendingConfirmation && _preparedAction is { } prepared)
            {
                mutation = prepared;
                _pendingConfirmation = null;
                if (!_load.TryBegin(true, out var started)) return CommandResult.KeepOpen();
                operation = started;
                _busy = true;
                _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.Loading(_repository, _number));
            }
            else
            {
                return CommandResult.KeepOpen();
            }
        }

        if (operation is { } startedOperation)
        {
            if (mutation is null)
            {
                CurrentWork = _load.Run(startedOperation, () => LoadAsync(startedOperation),
                    () => FinishOperation(startedOperation), "GitHub took too long to load pull request details.",
                    area: DiagnosticArea.PullRequests);
            }
            else
            {
                CurrentWork = _load.Run(startedOperation,
                    () => MutateAsync(mutation, _reviewDraft, startedOperation),
                    () => FinishOperation(startedOperation),
                    "GitHub took too long to respond. Refresh to check the pull request before trying again.",
                    area: DiagnosticArea.PullRequests, mutation: true);
            }
            IsLoading = true;
        }
        RaiseItemsChanged();
        return CommandResult.KeepOpen();
    }

    private void StartLoad()
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_disposed || _details is not null || _busy || _account is not { } account
                || !IsLive(account) || !_load.TryBegin(true, out operation))
                return;
        }
        CurrentWork = _load.Run(operation, () => LoadAsync(operation),
            () => FinishOperation(operation), "GitHub took too long to load pull request details.",
            area: DiagnosticArea.PullRequests);
        IsLoading = true;
    }

    private void StartLoadUnderLock(out ListLoadState.Operation? operation)
    {
        operation = null;
        if (_account is null || _busy || !_load.TryBegin(true, out var started)) return;
        operation = started;
        _busy = true;
        _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.Loading(_repository, _number));
    }

    private async Task LoadAsync(ListLoadState.Operation operation)
    {
        GitHubAccount? account;
        lock (_lock) account = _account;
        if (account is null || !IsLive(account)) return;
        var details = await _client.GetDetailsAsync(account, _repository, _number, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || !IsLive(account)) return;
            _details = details;
            if (_pendingReviewCreationUnknown && _reviewDraft is { } draft && _reviewDraftHeadSha is { } draftHead)
            {
                var foundPending = details.Reviews.LastOrDefault(review => review.State == "PENDING"
                    && string.Equals(review.Author, account.Login, StringComparison.OrdinalIgnoreCase)
                    && review.CommitId == draftHead
                    && string.Equals(review.Body ?? string.Empty, draft.Body, StringComparison.Ordinal));
                if (foundPending is not null)
                {
                    _pendingReview = new PendingPullRequestReview(foundPending.Id, draftHead, draft.Body);
                    _pendingReviewCreationUnknown = false;
                    _outcomeUnknown = false;
                }
            }
            if (_pendingReview is { } pendingReview)
            {
                var foundReview = details.Reviews.FirstOrDefault(review => review.Id == pendingReview.Id);
                if (foundReview is { State: not "PENDING" })
                {
                    _pendingReview = null;
                    _reviewDraft = null;
                    _reviewDraftHeadSha = null;
                    _feedback = $"Review submitted as {foundReview.State.ToLowerInvariant()}.";
                    _outcomeUnknown = false;
                }
                else if (foundReview is { State: "PENDING" })
                {
                    _outcomeUnknown = false;
                }
            }
            _load.Succeed(operation, null);
        }
        if (IsLive(account)) Owner?.ApplyPullRequestUpdate(account, _repository, this, details.PullRequest);
    }

    private async Task MutateAsync(string action, PullRequestReviewDraft? reviewDraft, ListLoadState.Operation operation)
    {
        GitHubAccount? account;
        PullRequestDetailsSnapshot? target;
        lock (_lock)
        {
            account = _account;
            target = _details;
        }
        if (account is null || target is null || !IsLive(account))
            throw new GitHubApiException("Your GitHub account changed. Refresh and review the pull request again.");

        string? feedback = null;
        try
        {
            if (!IsCurrentOperation(account, operation)) return;
            switch (action)
            {
                case "draft":
                    await _client.SetDraftAsync(account, _repository, _number, target.HeadSha, draft: true, operation.Token).ConfigureAwait(false);
                    feedback = "Pull request converted to draft.";
                    break;
                case "ready":
                    await _client.SetDraftAsync(account, _repository, _number, target.HeadSha, draft: false, operation.Token).ConfigureAwait(false);
                    feedback = "Pull request marked ready for review.";
                    break;
                case "update-branch":
                    var result = await _client.UpdateBranchAsync(account, _repository, _number, target.HeadSha, operation.Token).ConfigureAwait(false);
                    feedback = result.Pending
                        ? "GitHub accepted the update. The branch update is pending; refresh to check its result."
                        : result.HeadSha == target.HeadSha
                            ? "GitHub confirmed the update request. The head SHA has not changed yet."
                            : "GitHub merged the base branch into the pull request branch.";
                    break;
                case "review":
                    if (reviewDraft is null) throw new GitHubApiException("Review draft unavailable. Prepare the review again.");
                    PendingPullRequestReview? pending;
                    lock (_lock) pending = _pendingReview;
                    if (pending is null)
                    {
                        pending = await _client.CreatePendingReviewAsync(account, _repository, _number,
                            target.HeadSha, reviewDraft.Body, operation.Token).ConfigureAwait(false);
                        if (!IsCurrentOperation(account, operation)) return;
                        lock (_lock) _pendingReview = pending;
                    }
                    if (!IsCurrentOperation(account, operation)) return;
                    var submitted = await _client.SubmitReviewAsync(account, _repository, _number, pending,
                        reviewDraft.Event, operation.Token).ConfigureAwait(false);
                    feedback = $"Review submitted as {submitted.State.ToLowerInvariant()}.";
                    break;
            }
        }
        catch (GitHubApiException ex) when (ex.OutcomeUnknown)
        {
            lock (_lock)
            {
                if (_load.IsCurrent(operation) && IsLive(account))
                {
                    _outcomeUnknown = true;
                    if (action == "review" && _pendingReview is null)
                        _pendingReviewCreationUnknown = true;
                }
            }
            throw;
        }

        if (!IsCurrentOperation(account, operation)) return;
        var refreshed = await _client.GetDetailsAsync(account, _repository, _number, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || !IsLive(account)) return;
            _details = refreshed;
            _feedback = feedback;
            _preparedAction = null;
            if (action == "review")
            {
                _pendingReview = null;
                _reviewDraft = null;
                _reviewDraftHeadSha = null;
            }
            _load.Succeed(operation, null);
        }
        if (IsLive(account)) Owner?.ApplyPullRequestUpdate(account, _repository, this, refreshed.PullRequest);
    }

    // The owner list passes isCurrent so a replaced query or account generation can't mutate or publish here.
    private bool IsLive(GitHubAccount? account) =>
        account is not null && ReferenceEquals(account, _auth.CurrentAccount) && (_isCurrent?.Invoke() ?? true);

    private bool IsCurrentOperation(GitHubAccount account, ListLoadState.Operation operation)
    {
        lock (_lock)
            return _load.IsCurrent(operation) && ReferenceEquals(account, _account)
                && IsLive(account);
    }

    private void FinishOperation(ListLoadState.Operation operation)
    {
        lock (_lock)
        {
            if (!_load.IsCurrent(operation)) return;
            _busy = false;
            _feedback = _load.Error ?? _feedback;
            if (_load.Error is not null && _load.AuthorizeUrl is null)
                _outcomeUnknown |= _load.Error.Contains("outcome is unknown", StringComparison.OrdinalIgnoreCase)
                    || _load.Error.Contains("may have reached", StringComparison.OrdinalIgnoreCase);
            _form = _details is null
                ? new PullRequestDetailsForm(this, PullRequestDetailsCards.Error(_repository, _number, _load.Error ?? "Couldn't load pull request details."))
                : DetailsForm(_feedback, _load.AuthorizeUrl);
        }
        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private PullRequestDetailsForm DetailsForm(string? feedback, Uri? authorizeUrl = null) =>
        new(this, _details is null ? PullRequestDetailsCards.Error(_repository, _number, feedback ?? "Pull request details unavailable.")
            : PullRequestDetailsCards.Details(_repository, _details, _account?.Login, feedback, authorizeUrl,
                _outcomeUnknown, _reviewDraft, _pendingReview is not null));

    private static string ConfirmationTitle(string action) => action switch
    {
        "draft" => "Convert pull request to draft",
        "ready" => "Mark pull request ready for review",
        _ => "Update branch from base",
    };

    private static PullRequestReviewDraft? ReadReviewDraft(string inputs)
    {
        try
        {
            using var json = JsonDocument.Parse(inputs);
            var root = json.RootElement;
            var eventValue = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("event", out var eventProperty)
                && eventProperty.ValueKind == JsonValueKind.String ? eventProperty.GetString() : null;
            var body = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("reviewBody", out var bodyProperty)
                && bodyProperty.ValueKind == JsonValueKind.String ? bodyProperty.GetString() : null;
            if (eventValue is not ("APPROVE" or "REQUEST_CHANGES" or "COMMENT")) return null;
            if (eventValue != "APPROVE" && string.IsNullOrWhiteSpace(body)) return null;
            return new PullRequestReviewDraft(eventValue, body ?? string.Empty);
        }
        catch (JsonException) { return null; }
    }

    private void AccountChanged()
    {
        ICommand? contextualCodespace;
        lock (_lock)
        {
            if (_disposed) return;
            _load.Invalidate(reset: true);
            _account = null;
            _details = null;
            contextualCodespace = _contextualCodespaceCommand;
            _contextualCodespaceCommand = null;
            _reviewDraft = null;
            _reviewDraftHeadSha = null;
            _pendingReview = null;
            _pendingReviewCreationUnknown = false;
            _pendingConfirmation = null;
            _preparedAction = null;
            _busy = false;
            _outcomeUnknown = false;
            _form = new PullRequestDetailsForm(this, PullRequestDetailsCards.SignedOut());
        }
        (contextualCodespace as IDisposable)?.Dispose();
        IsLoading = false;
        RaiseItemsChanged();
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrEmpty(json) ? "{}" : json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed partial class PullRequestDetailsForm : FormContent
    {
        private readonly PullRequestDetailsPage _page;
        public PullRequestDetailsForm(PullRequestDetailsPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }
        public override ICommandResult SubmitForm(string inputs, string data) =>
            _page.Submit(this, inputs, data, ReadString(data, "confirmation"));
    }
}

internal sealed record PullRequestReviewDraft(string Event, string Body);

internal static class PullRequestDetailsCards
{
    internal static string Loading(string repository, int number) => Card(Text("Loading pull request details..."), Text($"{repository}#{number}"));
    internal static string SignedOut() => Card(Text("Sign in to view pull request details."));
    internal static string Error(string repository, int number, string message) => Card(
        Text($"{repository}#{number}"), Text(message), Submit("Refresh", "refresh"));

    internal static string Details(string repository, PullRequestDetailsSnapshot data, string? login, string? feedback,
        Uri? authorizeUrl, bool outcomeUnknown, PullRequestReviewDraft? draft, bool hasPendingReview)
    {
        var pull = data.PullRequest;
        var body = new List<string>
        {
            Text($"#{pull.Number} {pull.Title}"),
            Text($"{repository} · {pull.State} · {data.HeadSha} into {pull.BaseRef ?? pull.BaseBranch ?? "unknown"}"),
            Text($"Author: @{pull.Author ?? "unknown"} · Mergeable: {data.Mergeable?.ToString() ?? "unknown"} · Checks: {data.ChecksState}"),
            Text(pull.Body ?? "No description provided."),
            feedback is null ? string.Empty : Text(feedback),
            authorizeUrl is null ? string.Empty : Authorize(authorizeUrl),
        };
        if (outcomeUnknown)
            body.Add(Text("The last request may still be processing. Refresh and verify GitHub before trying it again."));
        body.AddRange(ActionCards(data, login, outcomeUnknown, draft, hasPendingReview));
        body.Add(Text("Changed files"));
        body.Add(data.FilesError is null ? Text($"{data.Files.Count} files") : Text(data.FilesError));
        foreach (var file in data.Files.Take(25))
            body.Add(Text($"{file.Status}: {file.FileName} (+{file.Additions}, -{file.Deletions})"));
        if (data.Files.Count > 25) body.Add(Text($"Showing 25 of {data.Files.Count} changed files."));
        body.Add(Text("Reviews"));
        body.Add(data.ReviewsError is null ? Text($"{data.Reviews.Count} reviews") : Text(data.ReviewsError));
        foreach (var review in data.Reviews.TakeLast(20))
            body.Add(Text($"{review.Author ?? "Unknown"} · {review.State} · {review.Body}"));
        body.Add(Text("Checks and commit statuses"));
        if (data.ChecksError is not null) body.Add(Text(data.ChecksError));
        else foreach (var check in data.Checks) body.Add(Text($"{check.Name}: {check.Status} {check.Conclusion}"));
        if (data.StatusError is not null) body.Add(Text(data.StatusError));
        else if (data.CommitStatus is not null) body.Add(Text($"Combined commit status: {data.CommitStatus}"));
        body.Add(Submit("Refresh pull request", "refresh"));
        return Card([.. body]);
    }

    internal static string Confirm(string repository, int number, string title, string headSha, string confirmation,
        string login, string host) => Card(
        Text(title),
        Text($"Account: {login}@{host} · Target: {repository}#{number}"),
        Text($"Inspected head commit: {headSha}"),
        Text(title == "Update branch from base"
            ? "GitHub will merge the base branch into the pull request branch. This is not a rebase. The expected head SHA is checked before the update."
            : "GitHub will change the pull request's draft status."),
        SubmitWithConfirmation("Confirm", "confirm", confirmation, "positive"),
        SubmitWithConfirmation("Cancel", "cancel", confirmation));

    internal static string ConfirmReview(string repository, int number, PullRequestReviewDraft draft, string headSha,
        string confirmation, string login, string host) => Card(
        Text($"Submit {draft.Event.ToLowerInvariant().Replace('_', ' ')} review"),
        Text($"{login}@{host} · {repository}#{number} · inspected commit {headSha}"),
        Text(draft.Body),
        Text("This creates a pending review pinned to the inspected commit, then submits that same review."),
        SubmitWithConfirmation("Confirm review", "confirm", confirmation, "positive"),
        SubmitWithConfirmation("Cancel", "cancel", confirmation));

    private static IEnumerable<string> ActionCards(
        PullRequestDetailsSnapshot details, string? login, bool unknown, PullRequestReviewDraft? draft, bool hasPendingReview)
    {
        var pull = details.PullRequest;
        if (details.CanWrite && (pull.State is SubjectState.Open or SubjectState.Draft) && !unknown)
        {
            yield return Submit(pull.State == Notifications.SubjectState.Draft ? "Mark ready for review" : "Convert to draft",
                pull.State == Notifications.SubjectState.Draft ? "ready" : "draft");
            yield return Submit("Update branch from base", "update-branch");
        }
        if (pull.State == SubjectState.Open
            && !string.Equals(login, pull.Author, StringComparison.OrdinalIgnoreCase) && !unknown)
        {
            yield return Text(hasPendingReview
                ? "A pending review draft is ready to submit again. It will not create a second draft."
                : "Review this pull request at the inspected head commit.");
            yield return $$"""{"type":"Input.ChoiceSet","id":"event","label":"Review action","style":"compact","value":"APPROVE","choices":[{"title":"Approve","value":"APPROVE"},{"title":"Request changes","value":"REQUEST_CHANGES"},{"title":"Comment","value":"COMMENT"}]}""";
            yield return $$"""{"type":"Input.Text","id":"reviewBody","label":"Review summary","isMultiline":true,"value":{{GitHubJson.String(draft?.Body ?? string.Empty)}}}""";
            yield return Submit(hasPendingReview ? "Submit pending review" : "Prepare review", "prepare-review");
        }
        else if (string.Equals(login, pull.Author, StringComparison.OrdinalIgnoreCase))
            yield return Text("You can't submit a review on your own pull request.");
    }

    private static string Submit(string title, string action) =>
        "{\"type\":\"ActionSet\",\"actions\":[{\"type\":\"Action.Submit\",\"title\":" + GitHubJson.String(title)
            + ",\"data\":{\"action\":" + GitHubJson.String(action) + "}}]}";
    private static string SubmitWithConfirmation(string title, string action, string confirmation, string? style = null)
    {
        var styleJson = style is null ? string.Empty : ",\"style\":" + GitHubJson.String(style);
        return "{\"type\":\"ActionSet\",\"actions\":[{\"type\":\"Action.Submit\",\"title\":" + GitHubJson.String(title)
            + styleJson + ",\"data\":{\"action\":" + GitHubJson.String(action) + ",\"confirmation\":"
            + GitHubJson.String(confirmation) + "}}]}";
    }
    private static string Authorize(Uri url) =>
        $$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Authorize organization access","url":{{GitHubJson.String(url.AbsoluteUri)}}}]}""";
    private static string Text(string text) => $$"""{"type":"TextBlock","text":{{GitHubJson.String(text)}},"wrap":true}""";
    private static string Card(params string[] items) => $$"""{"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(",", items.Where(item => !string.IsNullOrEmpty(item)))}}]}""";
}
