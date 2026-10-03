// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class WorkflowJobsPage : DynamicListPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IActionsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly GitHubAccount? _account;
    private readonly string _repository;
    private readonly GitHubWorkflowRun _run;
    private readonly PageEmptyContent _emptyContent;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<WorkflowJobItem> _items = [];
    private readonly Dictionary<long, WorkflowJobDetailsPage> _detailsPages = [];

    internal WorkflowJobsPage(
        AuthService auth, IActionsClient client, IBrowserLauncher browser, string repository, GitHubWorkflowRun run)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _repository = repository;
        _run = run;
        _account = auth.CurrentAccount;
        Id = $"{ActionsPage.PageId}.jobs.{Uri.EscapeDataString(repository)}.{run.Id}";
        Name = "Jobs and steps";
        Title = $"Jobs for {run.DisplayTitle}";
        Icon = Icons.Actions;
        PlaceholderText = "Filter workflow jobs...";
        _emptyContent = new PageEmptyContent(Icons.Actions, new RefreshWorkflowPageCommand(RefreshAsync));
        _accountSubscription = auth.Subscribe(this, static page => page.Dispose());
    }

    internal Task CurrentLoad => _load.CurrentLoad;

    public override IListItem[] GetItems()
    {
        if (_load.NeedsLoad)
        {
            StartLoad(reset: true);
        }
        IListItem[] items;
        bool loading;
        string? error;
        lock (_lock)
        {
            var terms = SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = _items
                .Where(item => item.Matches(terms))
                .Cast<IListItem>()
                .ToList();
            error = _load.Error;
            loading = _load.Fetching && current.Count == 0;
            if (error is not null)
            {
                current.Add(new ListItem(new NoOpCommand()) { Title = "Couldn't load workflow jobs", Subtitle = error, Icon = Icons.Actions });
            }
            items = [.. current];
        }

        EmptyContent = _emptyContent.Get(
            loading ? "Loading workflow jobs..." : error is null ? "No jobs in this run" : "Couldn't load workflow jobs",
            error ?? "This run doesn't include any jobs. Refresh to check again.", refresh: error is not null);
        return items;
    }

    public override void LoadMore() => StartLoad(reset: false);

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!_load.Disposed)
        {
            RaiseItemsChanged();
        }
    }

    private Task RefreshAsync()
    {
        lock (_lock)
        {
            _load.Invalidate(reset: true);
            _items.Clear();
        }

        return StartLoad(reset: true);
    }

    private Task StartLoad(bool reset)
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_account is null || _auth.CurrentAccount != _account || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(operation), () =>
        {
            bool hasMore;
            lock (_lock)
            {
                hasMore = _load.Error is null && _load.NextPage is not null;
            }

            _load.Publish(operation, () => HasMoreItems = hasMore);
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to return workflow jobs. Try refreshing.", area: DiagnosticArea.Actions);
    }

    private async Task LoadAsync(ListLoadState.Operation operation)
    {
        var result = await _client.GetJobsAsync(_account!, _repository, _run.Id, operation.Page, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || _auth.CurrentAccount != _account)
            {
                return;
            }

            if (operation.Reset)
            {
                _items.Clear();
            }

            var known = _items.Select(item => item.Job.Id).ToHashSet();
            _items.AddRange(result.Jobs.Where(job => known.Add(job.Id))
                .Select(job => new WorkflowJobItem(this, job)));
            _load.Succeed(operation, result.NextPage);
        }
    }

    internal WorkflowJobDetailsPage DetailsPage(GitHubWorkflowJob job)
    {
        lock (_lock)
        {
            if (!_detailsPages.TryGetValue(job.Id, out var page))
            {
                page = new WorkflowJobDetailsPage(_auth, _client, _browser, _repository, _run, job);
                _detailsPages.Add(job.Id, page);
            }

            return page;
        }
    }

    public void Dispose()
    {
        WorkflowJobDetailsPage[] pages;
        lock (_lock)
        {
            if (_load.Disposed)
            {
                return;
            }

            _load.Dispose();
            _items.Clear();
            pages = [.. _detailsPages.Values];
            _detailsPages.Clear();
        }

        _accountSubscription.Dispose();
        foreach (var page in pages)
        {
            page.Dispose();
        }

        HasMoreItems = false;
        IsLoading = false;
    }

    internal sealed partial class WorkflowJobItem : ListItem
    {
        public WorkflowJobItem(WorkflowJobsPage page, GitHubWorkflowJob job)
        {
            Job = job;
            Title = job.Name;
            Subtitle = JobSubtitle(job);
            Icon = job.Conclusion switch
            {
                "success" => Icons.RunSuccess,
                "failure" or "timed_out" => Icons.RunFailure,
                _ => Icons.RunNeutral,
            };
            Command = page.DetailsPage(job);
            MoreCommands =
            [
                new CommandContextItem(new OpenInBrowserCommand(page._browser, job.HtmlUrl ?? page._run.WebUrl, "Open on GitHub", Icons.Actions)),
            ];
        }

        public GitHubWorkflowJob Job { get; }

        public bool Matches(IEnumerable<string> terms)
        {
            var searchable = $"{Job.Name} {Job.Status} {Job.Conclusion} {string.Join(' ', Job.Steps.Select(step => $"{step.Name} {step.Status} {step.Conclusion}"))}";
            return terms.All(term => searchable.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static string JobSubtitle(GitHubWorkflowJob job)
        {
            var failed = job.Steps.Where(step => step.Conclusion is "failure" or "timed_out")
                .Select(step => step.Name).ToArray();
            return failed.Length > 0
                ? $"{job.Status}: {job.Conclusion ?? "in progress"} · Failed: {string.Join(", ", failed)}"
                : $"{job.Status}: {job.Conclusion ?? "in progress"} · {job.Steps.Count} steps";
        }
    }
}

internal sealed partial class WorkflowJobDetailsPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IActionsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly string _repository;
    private readonly GitHubWorkflowRun _run;
    private readonly GitHubWorkflowJob _initial;
    private readonly GitHubAccount? _account;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FormContent _form;
    private GitHubWorkflowJob? _job;
    private Task _currentOperation = Task.CompletedTask;
    private bool _loading;
    private bool _busy;
    private bool _submitted;
    private bool _disposed;
    private bool _confirming;

    internal WorkflowJobDetailsPage(
        AuthService auth, IActionsClient client, IBrowserLauncher browser, string repository, GitHubWorkflowRun run, GitHubWorkflowJob job)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _repository = repository;
        _run = run;
        _initial = job;
        _account = auth.CurrentAccount;
        Id = $"{ActionsPage.PageId}.job.{job.Id}";
        Name = job.Name;
        Title = job.Name;
        Icon = Icons.Actions;
        _form = new JobForm(this, WorkflowJobCards.Status(job, "Refreshing job and step status..."));
        _accountSubscription = auth.Subscribe(this, static page => page.Dispose());
    }

    internal Task CurrentOperation
    {
        get { lock (_lock) return _currentOperation; }
    }

    public override IContent[] GetContent()
    {
        lock (_lock)
        {
            if (!_loading && _job is null && !_disposed)
            {
                _loading = true;
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => LoadAsync(token));
            }

            return [_form];
        }
    }

    internal CommandResult Submit(string data)
    {
        var action = ReadAction(data);
        lock (_lock)
        {
            if (_disposed || _busy || _account is null || _auth.CurrentAccount != _account)
            {
                return CommandResult.KeepOpen();
            }

            if (action == "confirm" && _confirming && !_submitted && _job?.Status == "completed")
            {
                _busy = true;
                var token = _lifetime.Token;
                _currentOperation = Task.Run(() => RerunAsync(token));
            }
            else if (action == "rerun" && !_submitted && _job?.Status == "completed")
            {
                _confirming = true;
                _form = new JobForm(this, WorkflowJobCards.Confirm(_repository, _run, _job));
                RaiseAfterLock();
            }
            else if (action == "cancel" && _confirming)
            {
                _confirming = false;
                _form = new JobForm(this, WorkflowJobCards.Status(_job ?? _initial, null));
                RaiseAfterLock();
            }
        }

        if (_busy)
        {
            IsLoading = true;
            RaiseItemsChanged();
        }

        return CommandResult.KeepOpen();
    }

    private async Task LoadAsync(CancellationToken token)
    {
        try
        {
            var job = await _client.GetJobAsync(_account!, _repository, _initial.Id, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || token.IsCancellationRequested || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _job = job;
                _form = new JobForm(this, WorkflowJobCards.Status(job, null));
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
                    _form = new JobForm(this, WorkflowJobCards.Status(_initial, ex.Message));
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

    private async Task RerunAsync(CancellationToken token)
    {
        var message = "Checking the latest job status and repository access...";
        try
        {
            var current = await _client.GetJobAsync(_account!, _repository, _initial.Id, token).ConfigureAwait(false);
            if (current.Status != "completed"
                || current.Name != _initial.Name
                || current.Conclusion != _initial.Conclusion)
            {
                throw new GitHubApiException("The job changed after you opened it. Refresh the run and review the job again.");
            }

            if (!await _client.CanCancelAsync(_account!, _repository, token).ConfigureAwait(false))
            {
                throw new GitHubApiException("You need repository write access and Actions write permission to rerun this job.");
            }

            token.ThrowIfCancellationRequested();
            lock (_lock)
            {
                if (_disposed || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _submitted = true;
            }

            await _client.RerunJobAsync(_account!, _repository, current.Id, token).ConfigureAwait(false);
            var refreshed = await _client.GetJobAsync(_account!, _repository, current.Id, token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_disposed || _auth.CurrentAccount != _account)
                {
                    return;
                }

                _job = refreshed;
            }
            message = "GitHub accepted the rerun request. It also reruns jobs that depend on this job. Refresh the run to follow its status.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubApiException ex)
        {
            message = _submitted
                ? $"The rerun request may have been accepted. Refresh the run before trying again. {ex.Message}"
                : ex.Message;
            if (ex.OutcomeUnknown)
            {
                lock (_lock)
                {
                    if (!_disposed && !token.IsCancellationRequested && _auth.CurrentAccount == _account)
                    {
                        _submitted = true;
                    }
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
                if (!_disposed && _auth.CurrentAccount == _account)
                {
                    _confirming = false;
                    _form = new JobForm(this, WorkflowJobCards.Status(_job ?? _initial, message, allowRerun: !_submitted));
                }
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private void RaiseAfterLock() => Task.Run(() =>
    {
        lock (_lock)
        {
            if (_disposed || _auth.CurrentAccount != _account)
            {
                return;
            }
        }

        RaiseItemsChanged();
    });

    private static string? ReadAction(string data)
    {
        try
        {
            using var json = JsonDocument.Parse(data);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("action", out var action)
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
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
        }

        _accountSubscription.Dispose();
        _lifetime.Dispose();
    }

    private sealed partial class JobForm(WorkflowJobDetailsPage page, string template) : FormContent
    {
        public override string TemplateJson { get; set; } = template;
        public override ICommandResult SubmitForm(string inputs, string data) => page.Submit(data);
    }
}

internal static class WorkflowJobCards
{
    internal static string Status(GitHubWorkflowJob job, string? message, bool allowRerun = true) => Card(
    [
        Text(job.Name, large: true),
        Text($"Status: {job.Status}. Conclusion: {job.Conclusion ?? "not available"}"),
        .. job.Steps.Select(step => Text(
            $"{step.Number.ToString(CultureInfo.InvariantCulture)}. {step.Name}: {step.Status}, {step.Conclusion ?? "in progress"}")),
        message is null ? string.Empty : Text(message),
        allowRerun && job.Status == "completed"
            ? """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Rerun job...","data":{"action":"rerun"}}]}"""
            : string.Empty,
    ]);

    internal static string Confirm(string repository, GitHubWorkflowRun run, GitHubWorkflowJob job) => Card(
        Text($"Rerun {repository} / {run.Name} / {job.Name}?", large: true),
        Text("This uses GitHub Actions compute and may incur charges. Rerunning this job also reruns any jobs that depend on it."),
        Text($"Current result: {job.Conclusion ?? job.Status}."),
        """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Confirm rerun","data":{"action":"confirm"}},{"type":"Action.Submit","title":"Cancel","data":{"action":"cancel"}}]}""");

    private static string Text(string text, bool large = false) => $$"""
        {"type":"TextBlock","text":{{GitHubJson.String(text)}},"wrap":true{{(large ? ",\"size\":\"Large\",\"weight\":\"Bolder\"" : "")}}}
        """;

    private static string Card(params string[] elements) => $$"""
        {"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(',', elements.Where(item => item.Length > 0))}}]}
        """;
}

internal sealed partial class WorkflowArtifactsPage : DynamicListPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IActionsClient _client;
    private readonly IBrowserLauncher _browser;
    private readonly GitHubAccount? _account;
    private readonly string _repository;
    private readonly GitHubWorkflowRun _run;
    private readonly ListLoadState _load = new();
    private Lock _lock => _load.SyncRoot;
    private readonly List<WorkflowArtifactItem> _items = [];
    private readonly Dictionary<long, WorkflowDownloadPage> _downloadPages = [];
    private WorkflowDownloadPage? _logsPage;

    internal WorkflowArtifactsPage(AuthService auth, IActionsClient client, IBrowserLauncher browser, string repository, GitHubWorkflowRun run)
    {
        _auth = auth;
        _client = client;
        _browser = browser;
        _repository = repository;
        _run = run;
        _account = auth.CurrentAccount;
        Id = $"{ActionsPage.PageId}.artifacts.{Uri.EscapeDataString(repository)}.{run.Id}";
        Name = "Logs and artifacts";
        Title = $"Downloads for {run.DisplayTitle}";
        Icon = Icons.Actions;
        PlaceholderText = "Filter artifacts...";
        _accountSubscription = auth.Subscribe(this, static page => page.Dispose());
    }

    internal Task CurrentLoad => _load.CurrentLoad;

    public override IListItem[] GetItems()
    {
        if (_load.NeedsLoad)
        {
            StartLoad(reset: true);
        }
        lock (_lock)
        {
            var items = new List<IListItem>
            {
                new ListItem(LogsPage()) { Title = "Download workflow logs", Subtitle = "Save the run logs as a ZIP file", Icon = Icons.Actions },
            };
            var terms = SearchText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items.AddRange(_items.Where(item => item.Matches(terms)));
            if (_load.Error is { } error)
            {
                items.Add(new ListItem(new NoOpCommand()) { Title = "Couldn't load artifacts", Subtitle = error, Icon = Icons.Actions });
            }

            return [.. items];
        }
    }

    public override void LoadMore() => StartLoad(reset: false);

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (!_load.Disposed)
        {
            RaiseItemsChanged();
        }
    }

    private Task RefreshAsync()
    {
        lock (_lock)
        {
            _load.Invalidate(reset: true);
            _items.Clear();
        }

        return StartLoad(reset: true);
    }

    private Task StartLoad(bool reset)
    {
        ListLoadState.Operation operation;
        lock (_lock)
        {
            if (_account is null || _auth.CurrentAccount != _account || !_load.TryBegin(reset, out operation))
            {
                return _load.CurrentLoad;
            }
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, () => LoadAsync(operation), () =>
        {
            bool hasMore;
            lock (_lock) hasMore = _load.Error is null && _load.NextPage is not null;
            _load.Publish(operation, () => HasMoreItems = hasMore);
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to return workflow artifacts. Try refreshing.", area: DiagnosticArea.Actions);
    }

    private async Task LoadAsync(ListLoadState.Operation operation)
    {
        var result = await _client.GetArtifactsAsync(_account!, _repository, _run.Id, operation.Page, operation.Token).ConfigureAwait(false);
        lock (_lock)
        {
            if (!_load.IsCurrent(operation) || _auth.CurrentAccount != _account)
            {
                return;
            }

            if (operation.Reset) _items.Clear();
            var known = _items.Select(item => item.Artifact.Id).ToHashSet();
            _items.AddRange(result.Artifacts.Where(artifact => known.Add(artifact.Id))
                .Select(artifact => new WorkflowArtifactItem(this, artifact)));
            _load.Succeed(operation, result.NextPage);
        }
    }

    private WorkflowDownloadPage LogsPage()
    {
        lock (_lock)
        {
            return _logsPage ??= new WorkflowDownloadPage(_auth, _client, _repository, _run, null);
        }
    }

    internal WorkflowDownloadPage DownloadPage(GitHubArtifact artifact)
    {
        lock (_lock)
        {
            if (!_downloadPages.TryGetValue(artifact.Id, out var page))
            {
                page = new WorkflowDownloadPage(_auth, _client, _repository, _run, artifact);
                _downloadPages.Add(artifact.Id, page);
            }

            return page;
        }
    }

    public void Dispose()
    {
        WorkflowDownloadPage[] pages;
        lock (_lock)
        {
            if (_load.Disposed) return;
            _load.Dispose();
            _items.Clear();
            var pagesToDispose = _downloadPages.Values.ToList();
            if (_logsPage is not null)
            {
                pagesToDispose.Add(_logsPage);
            }

            pages = [.. pagesToDispose];
            _downloadPages.Clear();
            _logsPage = null;
        }

        _accountSubscription.Dispose();
        foreach (var page in pages) page.Dispose();
        HasMoreItems = false;
        IsLoading = false;
    }

    internal sealed partial class WorkflowArtifactItem : ListItem
    {
        public WorkflowArtifactItem(WorkflowArtifactsPage page, GitHubArtifact artifact)
        {
            Artifact = artifact;
            Title = artifact.Name;
            Subtitle = artifact.Expired
                ? "Expired and unavailable"
                : $"{artifact.SizeInBytes.ToString("N0", CultureInfo.CurrentCulture)} bytes";
            Icon = Icons.Actions;
            Command = artifact.Expired ? new NoOpCommand() : page.DownloadPage(artifact);
            MoreCommands =
            [
                new CommandContextItem(new OpenInBrowserCommand(page._browser, page._run.WebUrl, "Open workflow run", Icons.Actions)),
            ];
        }

        public GitHubArtifact Artifact { get; }

        public bool Matches(IEnumerable<string> terms) =>
            terms.All(term => $"{Artifact.Name} {Artifact.SizeInBytes} {Artifact.Expired}".Contains(
                term, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed partial class WorkflowDownloadPage : ContentPage, IDisposable
{
    private readonly AuthService _auth;
    private readonly IDisposable _accountSubscription;
    private readonly IActionsClient _client;
    private readonly string _repository;
    private readonly GitHubWorkflowRun _run;
    private readonly GitHubArtifact? _artifact;
    private readonly GitHubAccount? _account;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private FormContent _form;
    private string _status;
    private CancellationTokenSource? _downloadCancellation;
    private Task _currentOperation = Task.CompletedTask;
    private bool _busy;
    private bool _disposed;

    internal WorkflowDownloadPage(
        AuthService auth, IActionsClient client, string repository, GitHubWorkflowRun run, GitHubArtifact? artifact)
    {
        _auth = auth;
        _client = client;
        _repository = repository;
        _run = run;
        _artifact = artifact;
        _account = auth.CurrentAccount;
        _status = "Choose a destination file. Existing files at that path will be replaced after the download completes.";
        Id = $"{ActionsPage.PageId}.download.{run.Id}.{artifact?.Id ?? 0}";
        Name = "Save download";
        Title = artifact is null ? "Save workflow logs" : $"Save {artifact.Name}";
        Icon = Icons.Actions;
        _form = new DownloadForm(this, WorkflowDownloadCards.Form(Title, SuggestedPath(), _status));
        _accountSubscription = auth.Subscribe(this, static page => page.Dispose());
    }

    internal Task CurrentOperation
    {
        get { lock (_lock) return _currentOperation; }
    }

    public override IContent[] GetContent()
    {
        lock (_lock) return [_form];
    }

    private CommandResult Submit(string inputs, string data)
    {
        var action = ReadAction(data);
        if (action == "cancel")
        {
            lock (_lock)
            {
                if (_disposed || !_busy || _downloadCancellation is null)
                {
                    return CommandResult.KeepOpen();
                }

                _status = "Cancelling the download...";
                _form = new DownloadForm(this, WorkflowDownloadCards.Status(Title, SuggestedPath(), _status));
                _downloadCancellation.Cancel();
            }

            IsLoading = true;
            RaiseItemsChanged();
            return CommandResult.KeepOpen();
        }

        if (action != "save")
        {
            return CommandResult.KeepOpen();
        }

        string? destination;
        try
        {
            using var json = JsonDocument.Parse(inputs);
            destination = json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("destination", out var value)
                && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return CommandResult.KeepOpen();
        }

        CancellationToken token;
        lock (_lock)
        {
            if (_disposed || _busy || _account is null || _auth.CurrentAccount != _account
                || string.IsNullOrWhiteSpace(destination) || (_artifact?.Expired ?? false))
            {
                return CommandResult.KeepOpen();
            }

            _busy = true;
            _status = "Downloading. You can cancel below.";
            _form = new DownloadForm(this, WorkflowDownloadCards.Status(Title, destination, _status, cancellable: true));
            _downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            token = _downloadCancellation.Token;
            _currentOperation = Task.Run(() => DownloadAsync(destination.Trim(), token));
        }

        IsLoading = true;
        RaiseItemsChanged();
        return CommandResult.KeepOpen();
    }

    private async Task DownloadAsync(string destination, CancellationToken token)
    {
        string? status = null;
        try
        {
            await _client.DownloadAsync(_account!, _repository, _run.Id, _artifact?.Id, destination, token).ConfigureAwait(false);
            status = $"Saved to {destination}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                status = "Download canceled. No partial file was saved.";
            }
        }
        catch (GitHubApiException ex)
        {
            status = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            status = "Couldn't save the download. Check the destination path and folder permissions.";
        }
        finally
        {
            bool publish;
            lock (_lock)
            {
                _busy = false;
                _downloadCancellation?.Dispose();
                _downloadCancellation = null;
                publish = !_disposed && _auth.CurrentAccount == _account;
                if (status is not null && !_disposed && _auth.CurrentAccount == _account)
                {
                    _status = status;
                    _form = new DownloadForm(this, WorkflowDownloadCards.Status(Title, destination, status));
                }
            }

            if (publish)
            {
                IsLoading = false;
                RaiseItemsChanged();
            }
        }
    }

    private string SuggestedPath()
    {
        var file = _artifact is null
            ? $"workflow-{_run.Id.ToString(CultureInfo.InvariantCulture)}-logs.zip"
            : $"{SafeFileName(_artifact.Name)}.zip";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", file);
    }

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character));
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

    private sealed partial class DownloadForm(WorkflowDownloadPage page, string template) : FormContent
    {
        public override string TemplateJson { get; set; } = template;
        public override ICommandResult SubmitForm(string inputs, string data) => page.Submit(inputs, data);
    }
}

internal sealed partial class RefreshWorkflowPageCommand : InvokableCommand
{
    public RefreshWorkflowPageCommand(Func<Task> refresh) : base()
    {
        _refresh = refresh;
        Name = "Refresh";
        Icon = Icons.Refresh;
    }

    private readonly Func<Task> _refresh;

    public override ICommandResult Invoke()
    {
        _ = _refresh();
        return CommandResult.KeepOpen();
    }
}

internal static class WorkflowDownloadCards
{
    internal static string Form(string title, string destination, string status) => Card(
        Text(title, "Large"),
        Text(status),
        $$"""{"type":"Input.Text","id":"destination","label":"Save to","isRequired":true,"value":{{GitHubJson.String(destination)}}}""",
        Submit(cancellable: false));

    internal static string Status(string title, string destination, string status, bool cancellable = false) => Card(
        Text(title, "Large"),
        Text(status),
        $$"""{"type":"Input.Text","id":"destination","label":"Save to","isRequired":true,"value":{{GitHubJson.String(destination)}}}""",
        Submit(cancellable));

    private static string Text(string value, string? size = null) => $$"""
        {"type":"TextBlock","text":{{GitHubJson.String(value)}},"wrap":true{{(size is null ? string.Empty : $",\"size\":\"{size}\",\"weight\":\"Bolder\"")}}}
        """;

    private static string Submit(bool cancellable) => cancellable
        ? """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Save download","data":{"action":"save"}},{"type":"Action.Submit","title":"Cancel download","data":{"action":"cancel"}}]}"""
        : """{"type":"ActionSet","actions":[{"type":"Action.Submit","title":"Save download","data":{"action":"save"}}]}""";

    private static string Card(params string[] elements) => $$"""
        {"$schema":"http://adaptivecards.io/schemas/adaptive-card.json","type":"AdaptiveCard","version":"1.6","body":[{{string.Join(',', elements)}}]}
        """;
}
