// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class IssueConversationPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IIssueConversationClient _client;
    private readonly string _repository;
    private readonly int _number;
    private readonly string _kind;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly IDisposable _accountSubscription;
    private readonly List<IssueComment> _comments = [];
    private IssueConversationForm _form;
    private string _draft = string.Empty;
    private long? _editing;
    private long? _deleting;
    private GitHubAccount? _account;
    private readonly Func<bool>? _isCurrent;
    private bool _busy;
    private bool _disposed;
    private bool _outcomeUnknown;
    private bool _updated;
    private DiagnosticArea Area => _kind == "Pull request" ? DiagnosticArea.PullRequests : DiagnosticArea.Issues;

    internal Task CurrentWork { get; private set; } = Task.CompletedTask;

    public IssueConversationPage(
        AuthService auth, IIssueConversationClient client, GitHubAccount account, string repository, int number, string kind,
        Func<bool>? isCurrent = null, IconInfo? icon = null)
    {
        _isCurrent = isCurrent;
        _auth = auth;
        _client = client;
        _account = account;
        _repository = repository;
        _number = number;
        _kind = kind;
        Name = "Conversation";
        Title = $"{kind} conversation";
        Icon = icon ?? (kind == "Pull request" ? Icons.PullRequests : Icons.Issues);
        _form = new IssueConversationForm(this, IssueConversationCards.Loading(kind, repository, number));
        _accountSubscription = auth.Subscribe(this, static page => page.AccountChanged());
    }

    public override IContent[] GetContent()
    {
        bool needsLoad;
        lock (_lock) needsLoad = !_disposed && !_load.Loaded && !_load.Fetching;
        if (needsLoad) StartLoad(reset: true);
        lock (_lock) return [_form];
    }

    public void Dispose()
    {
        _accountSubscription.Dispose();
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _load.Dispose();
            _comments.Clear();
            _draft = string.Empty;
            _editing = null;
            _deleting = null;
            _form = new IssueConversationForm(this, IssueConversationCards.SignedOut());
        }
        IsLoading = false;
    }

    private CommandResult Submit(IssueConversationForm source, string inputs, string data)
    {
        var action = ReadString(data, "action");
        var commentId = ReadId(data, "id");
        ListLoadState.Operation? mutationOperation = null;
        bool loadMore = false;
        bool refresh = false;
        lock (_lock)
        {
            if (_disposed || _busy || !ReferenceEquals(source, _form) || _account is not { } account
                || !IsLive(account))
                return CommandResult.KeepOpen();
            if (_outcomeUnknown && action is "post" or "save-edit" or "confirm-delete")
                return CommandResult.KeepOpen();
            if (action == "cancel-delete")
            {
                _deleting = null;
                _form = Form(null);
            }
            else if (action == "delete")
            {
                if (commentId is null || _comments.All(item => item.Id != commentId)) return CommandResult.KeepOpen();
                _deleting = commentId;
                _form = Form(null);
            }
            else if (action == "confirm-delete")
            {
                if (_deleting is null || _deleting != commentId
                    || !_load.TryBegin(true, out var operation)) return CommandResult.KeepOpen();
                mutationOperation = operation;
                commentId = _deleting.Value;
                _busy = true;
                _form = new IssueConversationForm(this, IssueConversationCards.Loading(_kind, _repository, _number));
            }
            else if (action == "edit")
            {
                var comment = _comments.FirstOrDefault(item => item.Id == commentId);
                if (comment is null || !string.Equals(comment.Author, account.Login, StringComparison.OrdinalIgnoreCase))
                    return CommandResult.KeepOpen();
                _editing = commentId;
                _draft = comment.Body;
                _form = Form(null);
            }
            else if (action == "cancel-edit")
            {
                _editing = null;
                _draft = string.Empty;
                _form = Form(null);
            }
            else if (action is "post" or "save-edit")
            {
                _draft = ReadString(inputs, "body") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(_draft))
                {
                    _form = Form("Enter a comment before submitting.");
                }
                else if (action == "save-edit" && _editing is null)
                {
                    return CommandResult.KeepOpen();
                }
                else
                {
                    if (!_load.TryBegin(true, out var operation)) return CommandResult.KeepOpen();
                    if (action == "save-edit") commentId = _editing;
                    mutationOperation = operation;
                    _busy = true;
                    _form = new IssueConversationForm(this, IssueConversationCards.Loading(_kind, _repository, _number));
                }
            }
            else if (action == "load-more")
            {
                loadMore = true;
            }
            else if (action == "refresh")
            {
                refresh = true;
            }
            else
            {
                return CommandResult.KeepOpen();
            }

        }

        if (loadMore)
            return StartLoad(reset: false);
        if (refresh)
            return StartLoad(reset: true);

        if (mutationOperation is { } submittedOperation)
        {
            IsLoading = true;
            CurrentWork = _load.Run(submittedOperation,
                () => RunMutationAsync(action!, commentId, _draft, submittedOperation),
                () => FinishOperation(submittedOperation),
                "GitHub took too long to respond. Refresh before trying the comment again.",
                area: Area, mutation: action is "post" or "save-edit" or "confirm-delete");
        }
        RaiseItemsChanged();
        return CommandResult.KeepOpen();
    }

    private CommandResult StartLoad(bool reset)
    {
        GitHubAccount? account;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            account = _account;
            if (_disposed || account is null || !IsLive(account)
                || !_load.TryBegin(reset, out operation))
                return CommandResult.KeepOpen();
            if (reset) _comments.Clear();
            _busy = true;
            _form = new IssueConversationForm(this, IssueConversationCards.Loading(_kind, _repository, _number));
        }
        IsLoading = true;
        CurrentWork = _load.Run(operation, () => LoadAsync(account, operation, reset),
            () => FinishOperation(operation), "GitHub took too long to load the conversation comments.", area: Area);
        RaiseItemsChanged();
        return CommandResult.KeepOpen();
    }

    private async Task LoadAsync(GitHubAccount account, ListLoadState.Operation operation, bool reset)
    {
        var result = await _client.GetCommentsAsync(account, _repository, _number, operation.Page, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation)) return;
            if (reset) _comments.Clear();
            var known = _comments.Select(comment => comment.Id).ToHashSet();
            _comments.AddRange(result.Comments.Where(comment => known.Add(comment.Id)));
            _load.Succeed(operation, result.NextPage);
        }
    }

    private async Task RunMutationAsync(string action, long? commentId, string body, ListLoadState.Operation operation)
    {
        GitHubAccount? account;
        lock (_lock)
            account = _account;
        if (account is null || !IsCurrentOperation(account, operation))
            throw new GitHubApiException("Your GitHub account changed. Sign in and review the comment again.");
        try
        {
            if (!IsCurrentOperation(account, operation)) return;
            switch (action)
            {
                case "post":
                    await _client.CreateCommentAsync(account, _repository, _number, body, operation.Token).ConfigureAwait(false);
                    break;
                case "save-edit":
                    await _client.EditCommentAsync(account, _repository, _number, commentId!.Value, body, operation.Token).ConfigureAwait(false);
                    break;
                case "confirm-delete":
                    await _client.DeleteCommentAsync(account, _repository, _number, commentId!.Value, operation.Token).ConfigureAwait(false);
                    break;
            }
            if (!IsCurrentOperation(account, operation)) return;
            lock (_lock)
            {
                if (!_load.IsCurrent(operation) || !IsLive(account)) return;
                _draft = string.Empty;
                _editing = null;
                _deleting = null;
                _updated = true;
            }
        }
        catch (GitHubApiException ex) when (ex.OutcomeUnknown)
        {
            lock (_lock)
            {
                if (_load.IsCurrent(operation) && IsLive(account))
                    _outcomeUnknown = true;
            }
            throw;
        }
        if (!IsCurrentOperation(account, operation)) return;
        var result = await _client.GetCommentsAsync(account, _repository, _number, null, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || !IsLive(account)) return;
            _comments.Clear();
            _comments.AddRange(result.Comments);
            _draft = string.Empty;
            _editing = null;
            _deleting = null;
            _updated = true;
            _load.Succeed(operation, result.NextPage);
        }
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
            var feedback = _load.Error;
            if (_updated)
            {
                feedback = _load.Error is null
                    ? "Comment updated."
                    : "Comment saved, but comments could not be refreshed. Refresh before making another change.";
                _updated = false;
            }
            _form = Form(feedback, _load.AuthorizeUrl);
        }
        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private IssueConversationForm Form(string? feedback, Uri? authorizeUrl = null) =>
        new(this, _deleting is { } id
            ? IssueConversationCards.ConfirmDelete(_kind, _repository, _number, _comments.FirstOrDefault(comment => comment.Id == id),
                feedback, authorizeUrl)
            : IssueConversationCards.Comments(_kind, _repository, _number, _comments, _auth.CurrentAccount?.Login,
                _draft, _editing, _load.NextPage is not null, feedback, authorizeUrl, _outcomeUnknown));

    private void AccountChanged()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _load.Invalidate(reset: true);
            _account = _auth.CurrentAccount;
            _comments.Clear();
            _draft = string.Empty;
            _editing = null;
            _deleting = null;
            _busy = false;
            _outcomeUnknown = false;
            _updated = false;
            _form = new IssueConversationForm(this, IssueConversationCards.SignedOut());
        }
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

    private static long? ReadId(string json, string property) =>
        long.TryParse(ReadString(json, property), NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result : null;

    private sealed partial class IssueConversationForm : FormContent
    {
        private readonly IssueConversationPage _page;
        public IssueConversationForm(IssueConversationPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }
        public override ICommandResult SubmitForm(string inputs, string data) =>
            _page.Submit(this, inputs, data);
    }
}

internal static class IssueConversationCards
{
    internal static string Loading(string kind, string repository, int number) => Card(
        Text($"Loading {kind.ToLowerInvariant()} conversation..."), Text($"{repository}#{number}"));
    internal static string SignedOut() => Card(Text("Sign in to view conversation comments."));

    internal static string Comments(string kind, string repository, int number, IReadOnlyList<IssueComment> comments,
        string? login, string draft, long? editing, bool hasMore, string? feedback, Uri? authorizeUrl, bool outcomeUnknown)
    {
        var items = new List<string>
        {
            Text($"{kind} conversation. {repository}#{number}"),
            Text(feedback ?? string.Empty),
            outcomeUnknown ? Text("The last comment change may have reached GitHub. Verify it on GitHub before retrying; comment changes are locked to avoid duplicates.") : string.Empty,
            ActionSet(Submit("Refresh comments", "refresh")),
        };
        if (authorizeUrl is not null)
            items.Add($$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Authorize organization access","url":{{GitHubJson.String(authorizeUrl.AbsoluteUri)}}}]}""");
        foreach (var comment in comments)
        {
            items.Add(Text($"{comment.Author ?? "Unknown author"} · {comment.CreatedAt.ToLocalTime():g}"));
            items.Add(Text(comment.Body));
            var actions = new List<string>();
            if (!outcomeUnknown && string.Equals(comment.Author, login, StringComparison.OrdinalIgnoreCase))
                actions.Add(Submit("Edit", "edit", comment.Id));
            if (!outcomeUnknown) actions.Add(Submit("Delete", "delete", comment.Id));
            if (actions.Count > 0)
                items.Add($$"""{"type":"ActionSet","actions":[{{string.Join(",", actions)}}]}""");
        }
        if (hasMore) items.Add(ActionSet(Submit("Load more comments", "load-more")));
        if (!outcomeUnknown)
        {
            if (editing is not null) items.Add(ActionSet(Submit("Cancel edit", "cancel-edit")));
            items.Add($$"""{"type":"Input.Text","id":"body","label":"{{(editing is null ? "Comment" : "Edit comment")}}","isMultiline":true,"isRequired":true,"value":{{GitHubJson.String(draft)}}}""");
            items.Add(ActionSet(Submit(editing is null ? "Post comment" : "Save comment", editing is null ? "post" : "save-edit", style: "positive")));
        }
        return Card([.. items]);
    }

    internal static string ConfirmDelete(string kind, string repository, int number, IssueComment? comment, string? feedback, Uri? authorizeUrl) =>
        Card(
            Text($"Delete this {kind.ToLowerInvariant()} comment?"),
            Text($"{repository}#{number} · {comment?.Author ?? "Unknown author"}"),
            Text(comment?.Body ?? "Comment unavailable."),
            Text(feedback ?? string.Empty),
            authorizeUrl is null ? string.Empty
                : $$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Authorize organization access","url":{{GitHubJson.String(authorizeUrl.AbsoluteUri)}}}]}""",
            ActionSet(Submit("Confirm delete", "confirm-delete", comment?.Id, "destructive"), Submit("Cancel", "cancel-delete")));

    private static string ActionSet(params string[] actions) =>
        $$"""{"type":"ActionSet","actions":[{{string.Join(",", actions)}}]}""";
    private static string Submit(string title, string action, long? id = null, string? style = null) =>
        $$"""{"type":"Action.Submit","title":{{GitHubJson.String(title)}},"associatedInputs":"{{(action is "post" or "save-edit" ? "auto" : "none")}}","data":{"action":{{GitHubJson.String(action)}}{{(id is null ? string.Empty : $$""","id":"{{id.Value.ToString(CultureInfo.InvariantCulture)}}" """.Trim())}}}{{(style is null ? string.Empty : $$""","style":"{{style}}" """.Trim())}}}""";
    private static string Text(string text) => $$"""{"type":"TextBlock","text":{{GitHubJson.String(text)}},"wrap":true}""";
    private static string Card(params string[] items) => $$"""{"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(",", items.Where(item => !string.IsNullOrEmpty(item)))}}]}""";
}
