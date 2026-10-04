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
internal sealed partial class IssueDetailsPage : ContentPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.issue-details";

    internal bool IsDisposed => _load.Disposed;

    private readonly AuthService _auth;
    private readonly IIssuesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly IDisposable _accountSubscription;
    private Uri? _issueApiUrl;
    private GitHubIssue? _issue;
    private string? _repository;
    private (Uri ApiUrl, string Repository, Action OnOpened)? _notification;
    private bool _notificationActivated;
    private bool _notificationOpened;
    private IssueDetailsForm _form;

    public IssueDetailsPage(AuthService auth, IIssuesClient client, IBrowserLauncher browser, IIssueMutationsClient? mutationsClient = null)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _mutationClient = mutationsClient ?? client as IIssueMutationsClient;
        _mutations = _mutationClient is null ? null : new IssueMutationSession(auth, _mutationClient);
        Name = "Issue";
        Title = "Issue details";
        Icon = Icons.Issues;
        _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
        _accountSubscription = auth.Subscribe(this, static page => page.OnAccountChanged(null, EventArgs.Empty));
    }

    public override IContent[] GetContent()
    {
        ActivateIssue();
        ActivateNotification();
        lock (_lock)
        {
            return _form.ShowsDescription && _issue is { } issue
                ? [_form, new MarkdownContent(string.IsNullOrWhiteSpace(issue.Body) ? "No description provided." : issue.Body)]
                : [_form];
        }
    }

    internal IssueDetailsPage ForNotification(string notificationId, Uri issueApiUrl, string repository, Action onOpened, Action<GitHubIssue>? changed = null)
    {
        return new IssueDetailsPage(_auth, _client, _browser, _mutationClient)
        {
            _notification = (issueApiUrl, repository, onOpened),
            _changed = changed,
        };
    }

    internal void SetNotification(Uri issueApiUrl, string repository, Action onOpened, Action<GitHubIssue>? changed = null)
    {
        lock (_lock)
        {
            if (!_load.Disposed)
            {
                _notification = (issueApiUrl, repository, onOpened);
                _notificationOpened = false;
                _changed = changed;
            }
        }
    }

    internal Task CurrentLoad
    {
        get
        {
            lock (_lock)
            {
                return _load.CurrentLoad;
            }
        }
    }

    internal void LoadIssue(GitHubAccount account, Uri issueApiUrl, string repository)
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_load.Disposed || !ReferenceEquals(_auth.CurrentAccount, account))
            {
                return;
            }

            _load.Invalidate(reset: true);
            _targetRevision++;
            _review = null;
            _choices = null;
            _load.TryBegin(true, out operation);
            _loadedAccount = account;
            _issueApiUrl = issueApiUrl;
            _repository = repository;
            _issue = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.Loading());
        }

        _load.Publish(operation, () => IsLoading = true);
        _load.Publish(operation, PublishItemsChanged);
        _load.Run(operation, () => LoadAsync(account, issueApiUrl, repository, operation), () =>
        {
            string? error;
            lock (_lock)
            {
                error = _load.Error;
                if (_load.IsCurrent(operation) && error is not null)
                {
                    _form = new IssueDetailsForm(this, IssueDetailsCards.Error(error));
                }
            }

            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, PublishItemsChanged);
        }, "GitHub took too long to respond. Try loading the issue again.", area: DiagnosticArea.Issues);
    }

    internal void Open(GitHubAccount account, Uri issueApiUrl, string repository) =>
        LoadIssue(account, issueApiUrl, repository);

    internal static IssueDetailsPage ForIssue(AuthService auth, IIssuesClient client, IBrowserLauncher browser,
        GitHubAccount account, Uri issueApiUrl, string repository, Action<IssueDetailsPage, GitHubIssue> changed)
    {
        var page = new IssueDetailsPage(auth, client, browser)
        {
            _initialIssue = (account, issueApiUrl, repository),
        };
        page._changed = issue => changed(page, issue);
        return page;
    }

    internal ICommandResult HandleSubmit(string action)
    {
        if (_load.Disposed)
        {
            return CommandResult.KeepOpen();
        }

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
        bool load;
        lock (_lock)
        {
            if (_load.Disposed || _notificationOpened || _notification is not { } pending)
            {
                return;
            }

            load = !_notificationActivated;
            _notificationActivated = true;
            _notificationOpened = true;
            notification = pending;
        }

        if (_auth.CurrentAccount is { } account)
        {
            notification.Value.OnOpened();
            if (load)
            {
                LoadIssue(account, notification.Value.ApiUrl, notification.Value.Repository);
            }
        }
        else
        {
            OnAccountChanged(this, EventArgs.Empty);
        }
    }

    private async Task LoadAsync(GitHubAccount account, Uri issueApiUrl, string repository, ListLoadState.Operation operation)
    {
        var issue = await _client.GetIssueAsync(account, issueApiUrl, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation))
            {
                return;
            }

            _issue = issue;
            _form = new IssueDetailsForm(this, IssueDetailsCards.Details(repository, issue), showsDescription: true);
            _load.Succeed(operation, null);
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        long revision;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Invalidate(reset: true);
            _targetRevision++;
            _review = null;
            _choices = null;
            _loadedAccount = null;
            _initialIssue = null;
            _issueApiUrl = null;
            _issue = null;
            _repository = null;
            _notification = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
            revision = _load.Revision;
        }

        _load.Publish(revision, () => IsLoading = false);
        _load.Publish(revision, PublishItemsChanged);
    }

    public void Dispose()
    {
        _accountSubscription.Dispose();
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            _targetRevision++;
            _review = null;
            _choices = null;
            _loadedAccount = null;
            _initialIssue = null;
            _issueApiUrl = null;
            _issue = null;
            _repository = null;
            _notification = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
        }

        _mutations?.Dispose();
        IsLoading = false;
        Commands = [];
    }

    private sealed partial class IssueDetailsForm : FormContent
    {
        private readonly IssueDetailsPage _page;

        internal bool ShowsDescription { get; }

        public IssueDetailsForm(IssueDetailsPage page, string template, bool showsDescription = false)
        {
            _page = page;
            ShowsDescription = showsDescription;
            TemplateJson = template;
        }

        public override ICommandResult SubmitForm(string inputs, string data) => _page.Submit(this, inputs, ReadAction(data));

        private static string ReadAction(string data)
        {
            try
            {
                using var json = JsonDocument.Parse(string.IsNullOrEmpty(data) ? "{}" : data);
                return json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String
                    ? action.GetString() ?? string.Empty : string.Empty;
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
    public const string CloseCompleted = "closeCompleted";
    public const string CloseNotPlanned = "closeNotPlanned";
    public const string Reopen = "reopen";
    public const string AssignSelf = "assignSelf";
    public const string RemoveSelf = "removeSelf";
    public const string AddAssignee = "addAssignee";
    public const string RemoveAssignee = "removeAssignee";
    public const string AddLabel = "addLabel";
    public const string RemoveLabel = "removeLabel";
    public const string Select = "select";
    public const string Confirm = "confirmIssueChange";
}
