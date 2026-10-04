using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class AgentDetailsPage : ListPage, IDisposable
{
    private readonly IAgentBrowsingClient? _client;
    private readonly IBrowserLauncher _browser;
    private readonly GitHubAccount _account;
    private readonly Func<bool> _isCurrent;
    private readonly ListLoadState _load = new();
    private GitHubAgentTask _task;
    private readonly IDisposable? _subscription;

    internal AgentDetailsPage(IAgentBrowsingClient? client, GitHubAgentTask task, IBrowserLauncher browser,
        GitHubAccount account, Func<bool> isCurrent, AuthService? auth = null)
    {
        (_client, _task, _browser, _account, _isCurrent) = (client, task, browser, account, isCurrent);
        Name = "Task details";
        Title = task.Title;
        ShowDetails = true;
        Icon = Icons.Agents;
        _subscription = auth?.Subscribe(this, static page => page.OnAccountChanged());
    }

    internal Task CurrentLoad => _load.CurrentLoad;

    public override IListItem[] GetItems()
    {
        if (!_isCurrent() || _load.Disposed) { return []; }
        bool needsLoad;
        GitHubAgentTask task;
        string? error;
        lock (_load.SyncRoot) { needsLoad = _load.NeedsLoad; task = _task; error = _load.Error; }
        if (needsLoad && _client is not null) { _ = RefreshAsync(); }
        var items = new List<IListItem>
        {
            new ListItem(new OpenInBrowserCommand(new CurrentBrowser(this), task.WebUrl, "Open task on GitHub", Icons.Agents))
            {
                Title = task.Title,
                Subtitle = $"{AgentFormatting.StateText(task.State)} · {task.RepositoryFullName ?? "Repository unavailable"}",
                MoreCommands = [new CommandContextItem(new RefreshCommand(this))],
            },
        };
        if ((error ?? task.DetailsError) is { } message)
        {
            items.Add(new ListItem(new RefreshCommand(this)) { Title = "Task details unavailable", Subtitle = message });
        }

        foreach (var session in task.Sessions ?? [])
        {
            var usage = session.UsageAmount is { } amount
                ? $"{amount.ToString("G", CultureInfo.InvariantCulture)} {session.UsageType ?? "unknown units"}"
                : "Usage not returned";
            items.Add(new ListItem(new NoOpCommand())
            {
                Title = session.Name ?? (session.Id.Length == 0 ? "Session" : session.Id),
                Subtitle = $"{AgentFormatting.StateText(session.State)} · {session.Model ?? "Model not returned"} · {usage}",
                Details = new SessionDetails(session, usage),
                MoreCommands = [new CommandContextItem(new CopyTextCommand(session.Prompt ?? "") { Name = "Copy prompt" })],
            });
        }

        foreach (var artifact in task.Artifacts ?? [])
        {
            Uri? url = artifact.WebUrl;
            var title = $"{artifact.Provider} {artifact.Type} artifact";
            if (artifact.Provider == "github" && task.RepositoryFullName is { } repository)
            {
                var root = new Uri(_account.Host.WebUrl, repository + "/");
                if (artifact.Type == "branch" && artifact.HeadRef is { Length: > 0 } branch)
                {
                    url = new Uri(root, $"tree/{Uri.EscapeDataString(branch)}");
                    title = $"Branch: {branch}";
                }
                else if (artifact.Type == "pull")
                {
                    url ??= new Uri(root, "pulls");
                    title = $"Pull request artifact (ID {artifact.Id})";
                }
            }

            items.Add(new ListItem(url is null ? new NoOpCommand() : new OpenInBrowserCommand(new CurrentBrowser(this), url, "Open artifact", Icons.Agents))
            {
                Title = title,
                Subtitle = artifact.Type == "branch" ? $"Base: {artifact.BaseRef ?? "not returned"}"
                    : artifact.Type == "pull" ? artifact.WebUrl is not null ? "Open the generated pull request."
                        : "Open repository pull requests to inspect this artifact. Artifact IDs are not PR numbers."
                    : "This artifact type has no supported navigation.",
            });
        }

        return [.. items];
    }

    internal Task RefreshAsync()
    {
        ListLoadState.Operation operation;
        lock (_load.SyncRoot)
        {
            if (!_isCurrent() || _client is null || !_load.TryBegin(true, out operation)) { return CurrentLoad; }
        }

        _load.Publish(operation, () => IsLoading = true);
        return _load.Run(operation, async () =>
        {
            var result = await _client.GetTaskAsync(_account, _task, operation.Token).ConfigureAwait(false);
            lock (_load.SyncRoot)
            {
                if (!_load.IsCurrent(operation) || !_isCurrent()) { return; }
                _task = result;
                _load.Succeed(operation, null);
            }
        }, () =>
        {
            _load.Publish(operation, () => IsLoading = false);
            _load.Publish(operation, () => RaiseItemsChanged());
        }, "GitHub took too long to load task details.", area: DiagnosticArea.Agents);
    }

    private void OnAccountChanged()
    {
        lock (_load.SyncRoot)
        {
            _load.Invalidate(reset: true);
            _task = _task with { Sessions = [], Artifacts = [], DetailsError = null };
        }

        IsLoading = false;
        RaiseItemsChanged();
    }

    public void Dispose() { _subscription?.Dispose(); lock (_load.SyncRoot) { _load.Dispose(); } }

    private sealed partial class SessionDetails : Details
    {
        internal SessionDetails(AgentSession session, string usage)
        {
            Title = session.Name ?? session.Id;
            Body = $"{session.Prompt ?? "Prompt not returned."}\n\n{(session.Error is null ? "" : $"Error: {session.Error}\n\n")}"
                + $"State: {session.State}\nModel: {session.Model ?? "not returned"}\nUsage: {usage}\n"
                + $"Created: {Date(session.CreatedAt)}\nUpdated: {Date(session.UpdatedAt)}\nHead: {session.HeadRef ?? "not returned"}\nBase: {session.BaseRef ?? "not returned"}";
        }

        private static string Date(DateTimeOffset value) => value == DateTimeOffset.MinValue ? "not returned" : value.ToString("O", CultureInfo.InvariantCulture);
    }

    private sealed partial class RefreshCommand(AgentDetailsPage page) : InvokableCommand
    {
        public override string Name { get; set; } = "Refresh task details";
        public override ICommandResult Invoke() { _ = page.RefreshAsync(); return CommandResult.KeepOpen(); }
    }

    private sealed class CurrentBrowser(AgentDetailsPage page) : IBrowserLauncher
    {
        public void Open(Uri uri)
        {
            if (!page._load.Disposed && page._isCurrent()) { page._browser.Open(uri); }
        }
    }
}
