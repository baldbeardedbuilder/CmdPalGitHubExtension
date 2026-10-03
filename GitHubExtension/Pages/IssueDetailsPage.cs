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

    private readonly AuthService _auth;
    private readonly IIssuesClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly Lock _lock = new();
    private Uri? _issueApiUrl;
    private GitHubIssue? _issue;
    private string? _repository;
    private int _generation;
    private CancellationTokenSource? _loadCts;
    private volatile bool _disposed;
    private IssueDetailsForm _form;
    private Task _currentLoad = Task.CompletedTask;

    public IssueDetailsPage(AuthService auth, IIssuesClient client, IBrowserLauncher browser)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        Id = PageId;
        Name = "Issue";
        Title = "Issue details";
        Icon = Icons.Issues;
        _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
        _auth.AccountChanged += OnAccountChanged;
    }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            return [_form];
        }
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

    internal ICommandResult Open(GitHubAccount account, Uri issueApiUrl, string repository)
    {
        int generation;
        CancellationToken token;
        lock (_lock)
        {
            if (_disposed)
            {
                return CommandResult.KeepOpen();
            }

            CancelLoad();
            generation = ++_generation;
            _issueApiUrl = issueApiUrl;
            _repository = repository;
            _issue = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.Loading());
            _loadCts = new CancellationTokenSource();
            token = _loadCts.Token;
        }

        IsLoading = true;
        lock (_lock)
        {
            if (_disposed || generation != _generation)
            {
                return CommandResult.KeepOpen();
            }

            _currentLoad = Task.Run(() => LoadAsync(account, issueApiUrl, repository, generation, token));
        }

        return CommandResult.GoToPage(new GoToPageArgs { PageId = PageId });
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
                return Open(account, apiUrl, repository);
            }
        }

        return CommandResult.KeepOpen();
    }

    private async Task LoadAsync(GitHubAccount account, Uri issueApiUrl, string repository, int generation, CancellationToken token)
    {
        try
        {
            var issue = await _client.GetIssueAsync(account, issueApiUrl, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (generation != _generation || token.IsCancellationRequested || _disposed)
                {
                    return;
                }

                _issue = issue;
                _form = new IssueDetailsForm(this, IssueDetailsCards.Details(repository, issue));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (generation != _generation || _disposed)
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
                publish = generation == _generation && !_disposed;
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            CancelLoad();
            _issueApiUrl = null;
            _issue = null;
            _repository = null;
            _form = new IssueDetailsForm(this, IssueDetailsCards.SignedOut());
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    public void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelLoad();
        }

        IsLoading = false;
    }

    private void CancelLoad()
    {
        _generation++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
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
