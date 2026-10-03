// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class ContextualCodespacePage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly ICodespacesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly string _repository;
    private readonly int? _pullRequestNumber;
    private readonly string? _branch;
    private readonly GitHubAccount? _account;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FormContent _form;
    private GitHubCodespace? _existing;
    private GitHubCodespace? _created;
    private Task _currentOperation = Task.CompletedTask;
    private bool _loading;
    private bool _lookupCompleted;
    private bool _busy;
    private bool _reviewing;
    private bool _submitted;
    private bool _disposed;
    private string? _message;

    private ContextualCodespacePage(
        AuthService auth, ICodespacesClient client, IBrowserLauncher browser, string repository, string? branch, int? pullRequestNumber)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _repository = repository;
        _branch = branch;
        _pullRequestNumber = pullRequestNumber;
        _account = auth.CurrentAccount;
        Id = $"{CreateCodespacePage.PageId}.context.{Uri.EscapeDataString(repository)}.{pullRequestNumber ?? 0}";
        Name = "Open Codespace";
        Title = pullRequestNumber is null
            ? $"Codespace for {repository}"
            : $"Codespace for {repository} pull request {pullRequestNumber.Value}";
        Icon = Icons.Codespaces;
        _form = new ContextCodespaceForm(this, ContextCodespaceCards.Loading(Title));
        if (_account is not null && !_account.Host.IsGitHubDotCom)
        {
            _message = "Codespaces isn't available on GitHub Enterprise Server. Sign in to github.com.";
            _form = new ContextCodespaceForm(this, ContextCodespaceCards.Result(_repository, _message, null, false));
        }

        _accountSubscription = auth.Subscribe(this, static page => page.Dispose());
    }

    internal static ContextualCodespacePage ForRepository(
        AuthService auth, ICodespacesClient client, IBrowserLauncher browser, string repository, string? branch = null) =>
        new(auth, client, browser, repository, branch, null);

    internal static ContextualCodespacePage ForPullRequest(
        AuthService auth, ICodespacesClient client, IBrowserLauncher browser,
        string repository, int pullRequestNumber, string? headBranch = null) =>
        new(auth, client, browser, repository, headBranch, pullRequestNumber);

    internal Task CurrentOperation
    {
        get { lock (_lock) return _currentOperation; }
    }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            if (!_loading && !_lookupCompleted && _created is null && _message is null && !_disposed)
            {
                _loading = true;
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => FindExistingAsync(token));
            }

            return [_form];
        }
    }

    private CommandResult Submit(string data)
    {
        var action = ReadAction(data);
        GitHubCodespace? open = null;
        lock (_lock)
        {
            if (_disposed || _busy || _account is null || !_account.Host.IsGitHubDotCom || _auth.CurrentAccount != _account)
            {
                return CommandResult.KeepOpen();
            }

            if (action == "refresh")
            {
                _message = null;
                _existing = null;
                _lookupCompleted = false;
                _loading = true;
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => FindExistingAsync(token));
            }
            else if (action == "open" && (_created ?? _existing) is { } current)
            {
                open = current;
            }
            else if (action == "create" && !_submitted)
            {
                _reviewing = true;
                _form = new ContextCodespaceForm(this, ContextCodespaceCards.Confirm(_repository, _branch, _pullRequestNumber));
            }
            else if (action == "confirm-create" && _reviewing && !_submitted)
            {
                _reviewing = false;
                _busy = true;
                _loading = true;
                _form = new ContextCodespaceForm(this, ContextCodespaceCards.Loading("Creating a Codespace..."));
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => CreateAsync(token));
            }
            else if (action == "cancel" && _reviewing)
            {
                _reviewing = false;
                _form = new ContextCodespaceForm(this, ContextCodespaceCards.ExistingOrCreate(_repository, _branch,
                    _pullRequestNumber, _existing, null));
            }
        }

        if (open is not null)
        {
            _browser.Open(open.WebUrl);
            return CommandResult.Dismiss();
        }

        if (_busy)
        {
            IsLoading = true;
            RaiseItemsChanged();
        }
        else
        {
            RaiseItemsChanged();
        }

        return CommandResult.KeepOpen();
    }

    private async Task FindExistingAsync(CancellationToken token)
    {
        try
        {
            var spaces = new List<GitHubCodespace>();
            var visited = new HashSet<Uri>();
            Uri? page = null;
            do
            {
                var result = await _client.GetRepositoryCodespacesAsync(_account!, _repository, page, token).ConfigureAwait(false);
                spaces.AddRange(result.Codespaces);
                page = result.NextPage;
                if (page is { } next && !visited.Add(next))
                {
                    throw new GitHubApiException("GitHub returned a repeated codespace list page.");
                }
            }
            while (page is not null && spaces.Count <= 500);

            if (page is not null)
            {
                throw new GitHubApiException("This repository has too many Codespaces to check here. Open Codespaces on GitHub.");
            }

            var target = spaces
                .Where(space => string.Equals(space.RepositoryFullName, _repository, StringComparison.OrdinalIgnoreCase))
                .Where(space => _pullRequestNumber is not null
                    ? !string.IsNullOrWhiteSpace(_branch) && string.Equals(space.Branch, _branch, StringComparison.Ordinal)
                    : string.IsNullOrWhiteSpace(_branch) || string.Equals(space.Branch, _branch, StringComparison.Ordinal))
                .OrderByDescending(space => space.State == "Available")
                .ThenByDescending(space => space.LastUsedAt)
                .FirstOrDefault();
            lock (_lock)
            {
                if (_disposed || token.IsCancellationRequested || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _existing = target;
                _lookupCompleted = true;
                _form = new ContextCodespaceForm(this, ContextCodespaceCards.ExistingOrCreate(
                    _repository, _branch, _pullRequestNumber, _existing, null));
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
                    _lookupCompleted = true;
                    _message = ex.Message;
                    _form = new ContextCodespaceForm(this, ContextCodespaceCards.ExistingOrCreate(
                        _repository, _branch, _pullRequestNumber, null, ex.Message));
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

    private async Task CreateAsync(CancellationToken token)
    {
        var message = "GitHub is preparing the Codespace. Creation may take a few minutes.";
        try
        {
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_disposed || _auth.CurrentAccount != _account)
                {
                    return;
                }
            }

            var created = _pullRequestNumber is { } number
                ? await _client.CreatePullRequestCodespaceAsync(_account!, _repository, number, _branch, token).ConfigureAwait(false)
                : await _client.CreateRepositoryCodespaceAsync(_account!, _repository, _branch, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_disposed || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _submitted = true;
                _created = created;
                _existing = null;
                _message = null;
                _form = new ContextCodespaceForm(this, ContextCodespaceCards.Created(created));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubApiException ex)
        {
            message = ex.Message;
            lock (_lock)
            {
                if (!_disposed && _auth.CurrentAccount == _account)
                {
                    if (ex.OutcomeUnknown)
                    {
                        _submitted = true;
                    }

                    _message = message;
                    _form = new ContextCodespaceForm(this, ContextCodespaceCards.Result(_repository, message, ex.AuthorizeUrl, _submitted));
                }
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                _busy = false;
                publish = !_disposed && !token.IsCancellationRequested && _auth.CurrentAccount == _account;
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
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

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }

        _accountSubscription.Dispose();
        _lifetime.Dispose();
    }

    private sealed partial class ContextCodespaceForm(ContextualCodespacePage page, string template) : FormContent
    {
        public override string TemplateJson { get; set; } = template;
        public override ICommandResult SubmitForm(string inputs, string data) => page.Submit(data);
    }
}

internal static class ContextCodespaceCards
{
    internal static string Loading(string title) => Card(
        Text(title, "Large"),
        Text("Checking for a Codespace that matches this repository context..."));

    internal static string ExistingOrCreate(
        string repository, string? branch, int? pullRequestNumber, GitHubCodespace? existing, string? message)
    {
        var target = pullRequestNumber is { } number
            ? $"Pull request #{number}"
            : string.IsNullOrWhiteSpace(branch) ? repository : $"{repository} ({branch})";
        return Card(
            Text($"Codespace for {target}", "Large"),
            message is null ? string.Empty : Text(message, attention: true),
            existing is null
                ? Text("No matching Codespace was found. Creating one uses compute and storage and may incur charges.")
                : Text($"Found {existing.Name} on {existing.Branch ?? "unknown ref"}, state: {existing.State}."),
            existing is null
                ? """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Review Codespace creation","style":"positive","data":{"action":"create"}}]}"""
                : """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Open existing Codespace","style":"positive","data":{"action":"open"}},{"type":"Action.Submit","title":"Create another","data":{"action":"create"}}]}""");
    }

    internal static string Confirm(string repository, string? branch, int? pullRequestNumber)
    {
        var target = pullRequestNumber is { } number
            ? $"{repository} pull request #{number}"
            : string.IsNullOrWhiteSpace(branch) ? repository : $"{repository} ({branch})";
        return Card(
            Text("Confirm Codespace creation", "Large"),
            Text($"Target: {target}"),
            Text("This creates a Codespace using GitHub compute and storage, which may incur charges. Review the repository and pull request context before continuing."),
            """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Confirm creation","style":"positive","data":{"action":"confirm-create"}},{"type":"Action.Submit","title":"Cancel","data":{"action":"cancel"}}]}""");
    }

    internal static string Created(GitHubCodespace codespace) => Card(
        Text("Codespace created", "Large"),
        Text($"GitHub is preparing {codespace.Name} for {codespace.RepositoryFullName} on {codespace.Branch ?? "the selected context"}."),
        """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Open Codespace","style":"positive","data":{"action":"open"}}]}""");

    internal static string Result(string repository, string message, Uri? authorizeUrl, bool blocked) => Card(
        Text($"Codespace for {repository}", "Large"),
        Text(message, attention: true),
        authorizeUrl is null ? string.Empty
            : $$"""{"type":"ActionSet","actions":[{"type":"Action.OpenUrl","title":"Authorize organization access","url":{{GitHubJson.String(authorizeUrl.AbsoluteUri)}}}]}""",
        blocked ? Text("The create outcome is unknown. Check Codespaces on GitHub before trying again to avoid duplicate compute charges.")
            : string.Empty);

    private static string Text(string text, string? size = null, bool attention = false) => $$"""
        {"type":"TextBlock","text":{{GitHubJson.String(text)}},"wrap":true{{(size is null ? string.Empty : $",\"size\":\"{size}\",\"weight\":\"Bolder\"")}}{{(attention ? ",\"color\":\"Attention\"" : string.Empty)}}}
        """;

    private static string Card(params string[] body) => $$"""
        {"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(',', body.Where(item => item.Length > 0))}}]}
        """;
}
