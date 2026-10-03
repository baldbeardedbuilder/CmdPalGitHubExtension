// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class IssueWritePage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IIssueManagementClient _client;
    private readonly GitHubIssue? _expected;
    private readonly string _repository;
    private readonly Func<GitHubAccount, Task>? _created;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly IDisposable _accountSubscription;
    private readonly List<IssueMilestone> _milestones = [];
    private IssueWriteForm _form;
    private IssueWriteDraft? _draft;
    private GitHubAccount? _reviewAccount;
    private GitHubIssue? _savedIssue;
    private bool _reviewing;
    private bool _busy;
    private bool _outcomeUnknown;
    private bool _milestonesLoaded;
    private bool _disposed;

    internal Task CurrentWork { get; private set; } = Task.CompletedTask;
    internal RepositoryIssuesPage? Owner { get; init; }

    internal IssueWritePage(
        AuthService auth, IIssueManagementClient client, string repository, GitHubIssue? expected = null,
        Func<GitHubAccount, Task>? created = null)
    {
        _auth = auth;
        _client = client;
        _repository = repository;
        _expected = expected;
        _created = created;
        Id = $"com.baldbeardedbuilder.cmdpal.github.issue-write.{Uri.EscapeDataString(repository)}.{expected?.Number ?? 0}.{Guid.NewGuid():N}";
        Name = expected is null ? "Create issue" : "Edit issue";
        Title = $"{Name} in {repository}";
        Icon = Icons.Issues;
        _form = new IssueWriteForm(this, IssueWriteCards.Loading(repository, expected is not null));
        _accountSubscription = auth.Subscribe(this, static page => page.AccountChanged());
    }

    public override IContent[] GetContent()
    {
        StartLoad();
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
            _milestones.Clear();
            _draft = null;
            _reviewAccount = null;
            _form = new IssueWriteForm(this, IssueWriteCards.SignedOut());
        }
        IsLoading = false;
    }

    private CommandResult Submit(IssueWriteForm source, string inputs, string data)
    {
        var action = ReadString(data, "action");
        var result = ReadDraft(inputs);
        ListLoadState.Operation? operation = null;
        lock (_lock)
        {
            if (_disposed || _busy || !ReferenceEquals(source, _form)
                || _auth.CurrentAccount is not { } account || !_milestonesLoaded)
                return CommandResult.KeepOpen();
            if (action == "back" && _reviewing)
            {
                _reviewing = false;
                _form = Form(null);
            }
            else if (action == "review")
            {
                _draft = result;
                _reviewAccount = account;
                if (_outcomeUnknown)
                {
                    _form = Form("GitHub may have accepted the previous save. Check the issue before trying again.");
                }
                else if (result is null || string.IsNullOrWhiteSpace(result.Title))
                {
                    _form = Form("Enter an issue title and choose a valid milestone.");
                }
                else
                {
                    _reviewing = true;
                    _form = new IssueWriteForm(this, IssueWriteCards.Review(_repository, account, result,
                        MilestoneTitle(result.MilestoneNumber), _expected is not null));
                }
            }
            else if (action == "confirm" && _reviewing && _draft is not null
                && ReferenceEquals(account, _reviewAccount) && !_outcomeUnknown
                && _load.TryBegin(true, out var started))
            {
                operation = started;
                _busy = true;
                _form = new IssueWriteForm(this, IssueWriteCards.Loading(_repository, _expected is not null));
            }
            else if (action == "cancel")
            {
                _reviewing = false;
                _form = Form(null);
            }
            else if (action is not ("confirm" or "back" or "cancel"))
            {
                return CommandResult.KeepOpen();
            }
        }

        if (operation is { } writeOperation)
        {
            IsLoading = true;
            CurrentWork = _load.Run(writeOperation, () => SaveAsync(_draft!, _reviewAccount!, writeOperation),
                () => FinishOperation(writeOperation), "GitHub took too long to respond. Refresh to check the issue before retrying.",
                area: DiagnosticArea.Issues, mutation: true);
        }
        RaiseItemsChanged();
        return CommandResult.KeepOpen();
    }

    private void StartLoad()
    {
        GitHubAccount? account;
        ListLoadState.Operation operation;
        lock (_lock)
        {
            account = _auth.CurrentAccount;
            if (_disposed || account is null || _milestonesLoaded || !_load.TryBegin(true, out operation))
                return;
            _busy = true;
            _form = new IssueWriteForm(this, IssueWriteCards.Loading(_repository, _expected is not null));
        }
        IsLoading = true;
        CurrentWork = _load.Run(operation, () => LoadMilestonesAsync(account, operation),
            () => FinishOperation(operation), "GitHub took too long to load milestones.", area: DiagnosticArea.Issues);
        RaiseItemsChanged();
    }

    private async Task LoadMilestonesAsync(GitHubAccount account, ListLoadState.Operation operation)
    {
        var found = new Dictionary<int, IssueMilestone>();
        var visited = new HashSet<Uri>();
        Uri? page = null;
        do
        {
            var result = await _client.GetMilestonesAsync(account, _repository, page, operation.Token).ConfigureAwait(false);
            foreach (var milestone in result.Milestones)
            {
                if (milestone.State == "open" || milestone.Number == _expected?.MilestoneNumber)
                    found[milestone.Number] = milestone;
            }
            page = result.NextPage;
            if (page is not null && !visited.Add(page))
                throw new GitHubApiException("GitHub repeated a milestone page. Refresh before continuing.");
        }
        while (page is not null);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation)) return;
            _milestones.Clear();
            _milestones.AddRange(found.Values.OrderBy(milestone => milestone.Title, StringComparer.OrdinalIgnoreCase));
            _milestonesLoaded = true;
            _load.Succeed(operation, null);
        }
    }

    private async Task SaveAsync(IssueWriteDraft draft, GitHubAccount account, ListLoadState.Operation operation)
    {
        if (!ReferenceEquals(account, _auth.CurrentAccount))
            throw new GitHubApiException("Your GitHub account changed. Review the issue again before saving.");
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || !ReferenceEquals(account, _reviewAccount))
                return;
        }
        GitHubIssue saved;
        try
        {
            saved = _expected is null
                ? await _client.CreateIssueAsync(account, _repository, draft.Title, draft.Body, draft.MilestoneNumber, operation.Token).ConfigureAwait(false)
                : await _client.UpdateIssueAsync(account, _repository, _expected, draft.Title, draft.Body, draft.MilestoneNumber, operation.Token).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.OutcomeUnknown)
        {
            lock (_lock)
            {
                if (_load.IsCurrent(operation) && ReferenceEquals(account, _auth.CurrentAccount))
                    _outcomeUnknown = true;
            }
            throw;
        }
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || !ReferenceEquals(account, _auth.CurrentAccount)) return;
            _savedIssue = saved;
            _draft = draft;
            _load.Succeed(operation, null);
        }
        if (_expected is null && _created is not null && IsCurrentOperation(account, operation))
            await _created(account).ConfigureAwait(false);
    }

    private bool IsCurrentOperation(GitHubAccount account, ListLoadState.Operation operation)
    {
        lock (_lock)
            return _load.IsCurrent(operation) && ReferenceEquals(account, _reviewAccount)
                && ReferenceEquals(account, _auth.CurrentAccount);
    }

    private void FinishOperation(ListLoadState.Operation operation)
    {
        lock (_lock)
        {
            if (!_load.IsCurrent(operation)) return;
            _busy = false;
            if (_load.Error is { } error)
            {
                _outcomeUnknown |= operation.Reset && _load.AuthorizeUrl is null
                    && error.Contains("outcome is unknown", StringComparison.OrdinalIgnoreCase);
                _form = Form(error, _load.AuthorizeUrl);
            }
            else if (_savedIssue is { } saved)
            {
                _reviewing = false;
                _form = new IssueWriteForm(this, IssueWriteCards.Saved(saved));
            }
            else
            {
                _form = Form(null);
            }
        }
        _load.Publish(operation, () => IsLoading = false);
        _load.Publish(operation, () => RaiseItemsChanged());
    }

    private IssueWriteDraft? ReadDraft(string inputs)
    {
        try
        {
            using var json = JsonDocument.Parse(inputs);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            var title = root.TryGetProperty("title", out var titleValue) && titleValue.ValueKind == JsonValueKind.String
                ? titleValue.GetString() ?? string.Empty : string.Empty;
            var body = root.TryGetProperty("body", out var bodyValue) && bodyValue.ValueKind == JsonValueKind.String
                ? bodyValue.GetString() : string.Empty;
            int? milestoneNumber = null;
            if (root.TryGetProperty("milestone", out var selection) && selection.ValueKind == JsonValueKind.String
                && int.TryParse(selection.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index <= SelectableMilestones().Count && index > 0)
                milestoneNumber = SelectableMilestones()[index - 1].Number;
            return new IssueWriteDraft(title.Trim(), body, milestoneNumber);
        }
        catch (JsonException) { return null; }
    }

    private string MilestoneTitle(int? number) =>
        number is null ? "No milestone" : SelectableMilestones().FirstOrDefault(item => item.Number == number)?.Title ?? "Milestone unavailable";

    private IssueWriteForm Form(string? feedback, Uri? authorizeUrl = null) =>
        new(this, IssueWriteCards.Form(_repository, _expected, _draft, SelectableMilestones(), feedback, authorizeUrl,
            _outcomeUnknown));

    private List<IssueMilestone> SelectableMilestones()
    {
        var milestones = _milestones.Where(item => item.State == "open").ToList();
        if (_expected?.MilestoneNumber is { } currentNumber
            && _milestones.FirstOrDefault(item => item.Number == currentNumber) is { State: "closed" } closed)
            milestones.Add(closed);
        return milestones;
    }

    private void AccountChanged()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _load.Invalidate(reset: true);
            _milestones.Clear();
            _milestonesLoaded = false;
            _draft = null;
            _reviewAccount = null;
            _savedIssue = null;
            _reviewing = false;
            _busy = false;
            _outcomeUnknown = false;
            _form = new IssueWriteForm(this, IssueWriteCards.SignedOut());
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

    private sealed partial class IssueWriteForm : FormContent
    {
        private readonly IssueWritePage _page;
        public IssueWriteForm(IssueWritePage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }
        public override ICommandResult SubmitForm(string inputs, string data) =>
            _page.Submit(this, inputs, data);
    }
}

internal sealed record IssueWriteDraft(string Title, string? Body, int? MilestoneNumber);

internal static class IssueWriteCards
{
    internal static string Loading(string repository, bool edit) => Card(Text(edit ? "Loading issue editor..." : "Loading milestones..."), Text(repository));
    internal static string SignedOut() => Card(Text("Sign in to create or edit an issue."));

    internal static string Form(string repository, GitHubIssue? issue, IssueWriteDraft? draft,
        IReadOnlyList<IssueMilestone> milestones, string? feedback, Uri? authorizeUrl, bool outcomeUnknown)
    {
        var choices = new[] { (Number: (int?)null, Title: "No milestone") }
            .Concat(milestones.Select(item => (Number: (int?)item.Number,
                Title: item.State == "closed" ? item.Title + " (closed)" : item.Title))).ToArray();
        var selected = draft?.MilestoneNumber ?? issue?.MilestoneNumber;
        var selectedIndex = Array.FindIndex(choices, item => item.Number == selected);
        if (selectedIndex < 0) selectedIndex = 0;
        var elements = new List<string>
        {
            Text(issue is null ? "Create issue" : "Edit issue"),
            Text(repository),
            outcomeUnknown ? Text("GitHub may have accepted the previous save. Check the issue on GitHub before trying again.") : string.Empty,
            feedback is null ? string.Empty : Text(feedback),
            authorizeUrl is null ? string.Empty : Authorize(authorizeUrl),
            $$"""{"type":"Input.Text","id":"title","label":"Title","isRequired":true,"value":{{GitHubJson.String(draft?.Title ?? issue?.Title ?? string.Empty)}}}""",
            $$"""{"type":"Input.Text","id":"body","label":"Description","isMultiline":true,"value":{{GitHubJson.String(draft?.Body ?? issue?.Body ?? string.Empty)}}}""",
            $$"""{"type":"Input.ChoiceSet","id":"milestone","label":"Milestone","style":"compact","value":"{{selectedIndex.ToString(CultureInfo.InvariantCulture)}}","choices":[{{string.Join(",", choices.Select((item, index) => $$"""{"title":{{GitHubJson.String(item.Title)}},"value":"{{index.ToString(CultureInfo.InvariantCulture)}}"}"""))}}]}""",
            Text("Issue templates and forms aren't applied in this editor. Use GitHub when the repository requires a structured issue."),
            Submit("Review issue", "review"),
        };
        return Card([.. elements]);
    }

    internal static string Review(string repository, GitHubAccount account, IssueWriteDraft draft, string milestone, bool edit) => Card(
        Text(edit ? "Review issue changes" : "Review new issue"),
        Text($"{account.Login}@{account.Host.Name} · {repository} · {milestone}"),
        Text(draft.Title),
        Text(draft.Body ?? "No description provided."),
        Text("This editor supports title, description, and milestone only. Repository issue forms and unsupported fields need GitHub."),
        Submit("Edit", "back"),
        Submit(edit ? "Save issue" : "Create issue", "confirm", "positive"),
        Submit("Cancel", "cancel"));

    internal static string Saved(GitHubIssue issue) => Card(
        Text("Issue saved"),
        Text($"#{issue.Number} {issue.Title}"),
        $$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Open issue on GitHub","url":{{GitHubJson.String(issue.WebUrl.AbsoluteUri)}}}]}""");

    private static string Submit(string title, string action, string? style = null)
    {
        var styleJson = style is null ? string.Empty : ",\"style\":" + GitHubJson.String(style);
        return "{\"type\":\"Action.Submit\",\"title\":" + GitHubJson.String(title) + styleJson
            + ",\"data\":{\"action\":" + GitHubJson.String(action) + "}}";
    }
    private static string Authorize(Uri url) =>
        $$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Authorize organization access","url":{{GitHubJson.String(url.AbsoluteUri)}}}]}""";
    private static string Text(string text) => $$"""{"type":"TextBlock","text":{{GitHubJson.String(text)}},"wrap":true}""";
    private static string Card(params string[] items) => $$"""{"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(",", items.Where(item => !string.IsNullOrEmpty(item)))}}]}""";
}
