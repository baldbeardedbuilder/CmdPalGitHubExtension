// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// A shared detail view for issues opened from anywhere in the extension.
/// </summary>
internal sealed partial class IssueDetailsPage : ContentPage
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.issue-details";

    private readonly AuthService _auth;
    private readonly IIssuesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly Lock _lock = new();
    private Uri? _issueApiUrl;
    private GitHubIssue? _issue;
    private string? _repository;
    private (Uri ApiUrl, string Repository, Action OnOpened)? _notification;
    private bool _notificationActivated;
    private int _generation;
    private IssueDetailsForm _form;
    private Task _currentLoad = Task.CompletedTask;

    public IssueDetailsPage(AuthService auth, IIssuesClient client, IBrowserLauncher browser)
        : this(auth, client, browser, listenForAccountChanges: true)
    {
    }

    private IssueDetailsPage(AuthService auth, IIssuesClient client, IBrowserLauncher browser, bool listenForAccountChanges)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        Id = PageId;
        Name = "Issue";
        Title = "Issue details";
        Icon = Icons.Issues;
        _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
        if (listenForAccountChanges)
        {
            _auth.AccountChanged += OnAccountChanged;
        }
    }

    public override IContent[] GetContent()
    {
        ActivateNotification();
        lock (_lock)
        {
            return [_form];
        }
    }

    internal IssueDetailsPage ForNotification(string notificationId, Uri issueApiUrl, string repository, Action onOpened)
    {
        return new IssueDetailsPage(_auth, _client, _browser, listenForAccountChanges: false)
        {
            Id = $"{PageId}.{Uri.EscapeDataString(notificationId)}",
            _notification = (issueApiUrl, repository, onOpened),
        };
    }

    internal Task CurrentLoad
    {
        get
        {
            lock (_lock)
            {
                return _currentLoad;
            }
        }
    }

    internal void LoadIssue(GitHubAccount account, Uri issueApiUrl, string repository)
    {
        int generation;
        lock (_lock)
        {
            generation = ++_generation;
            _issueApiUrl = issueApiUrl;
            _repository = repository;
            _issue = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.Loading());
        }

        IsLoading = true;
        lock (_lock)
        {
            _currentLoad = Task.Run(() => LoadAsync(account, issueApiUrl, repository, generation));
        }
    }

    internal ICommandResult HandleSubmit(string action)
    {
        if (action == IssueDetailsActions.OpenInBrowser)
        {
            Uri? url;
            lock (_lock)
            {
                url = _issue?.WebUrl;
            }

            if (url is not null)
            {
                _browser.Open(url);
                return CommandResult.Dismiss();
            }
        }
        else if (action == IssueDetailsActions.Retry && _auth.CurrentAccount is { } account)
        {
            Uri? apiUrl;
            string? repository;
            lock (_lock)
            {
                apiUrl = _issueApiUrl;
                repository = _repository;
            }

            if (apiUrl is not null && repository is not null)
            {
                LoadIssue(account, apiUrl, repository);
            }
        }

        return CommandResult.KeepOpen();
    }

    private void ActivateNotification()
    {
        (Uri ApiUrl, string Repository, Action OnOpened)? notification;
        lock (_lock)
        {
            if (_notificationActivated || _notification is not { } pending)
            {
                return;
            }

            _notificationActivated = true;
            notification = pending;
        }

        if (_auth.CurrentAccount is { } account)
        {
            notification.Value.OnOpened();
            LoadIssue(account, notification.Value.ApiUrl, notification.Value.Repository);
        }
        else
        {
            OnAccountChanged(this, EventArgs.Empty);
        }
    }

    private async Task LoadAsync(GitHubAccount account, Uri issueApiUrl, string repository, int generation)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.PageLoad, DiagnosticArea.Issues, verbose: true);
        Exception? failure = null;
        try
        {
            var issue = await _client.GetIssueAsync(account, issueApiUrl, CancellationToken.None).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                _issue = issue;
                _form = new IssueDetailsForm(this, IssueDetailsCards.Details(repository, issue));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            lock (_lock)
            {
                if (generation != _generation)
                {
                    return;
                }

                _form = new IssueDetailsForm(this, IssueDetailsCards.Error(ex.Message));
            }
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                publish = generation == _generation;
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }

            lock (_lock)
            {
                publish = generation == _generation;
            }

            PageDiagnostics.Finish(operation, failure, publish);
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _generation++;
            _issueApiUrl = null;
            _issue = null;
            _repository = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    private sealed partial class IssueDetailsForm : FormContent
    {
        private readonly IssueDetailsPage _page;

        public IssueDetailsForm(IssueDetailsPage page, string template)
        {
            _page = page;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.HandleSubmit(ReadAction(data));

        private static string ReadAction(string data)
        {
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrEmpty(data) ? "{}" : data);
                return json.RootElement.TryGetProperty("action", out var action) ? action.GetString() ?? string.Empty : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }
    }
}

internal static class IssueDetailsActions
{
    public const string OpenInBrowser = "openInBrowser";
    public const string Retry = "retry";
}
