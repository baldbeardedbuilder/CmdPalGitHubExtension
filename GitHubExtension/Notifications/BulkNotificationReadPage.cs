using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal sealed partial class BulkNotificationReadPage : ListPage, IDisposable
{
    private readonly GitHubAccount _account;
    private readonly INotificationBrowsingClient? _client;
    private readonly MutationExecutor _executor;
    private readonly string[] _repositories;
    private readonly Func<bool> _isCurrent;
    private readonly Func<Task> _refresh;
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset _cutoff;
    private string _message = "";
    private Uri? _authorize;
    private bool _disposed;
    private string? _customRepository;

    internal BulkNotificationReadPage(GitHubAccount account, INotificationBrowsingClient? client, MutationExecutor executor,
        DateTimeOffset cutoff, string[] repositories, Func<bool> isCurrent, Func<Task> refresh)
    {
        (_account, _client, _executor, _cutoff, _repositories, _isCurrent, _refresh) =
            (account, client, executor, cutoff, repositories, isCurrent, refresh);
        Name = "Mark notifications read in bulk";
        Title = Name;
        Icon = Icons.Notifications;
    }

    public override IListItem[] GetItems()
    {
        if (_disposed || !_isCurrent()) { return []; }
        var items = new List<IListItem>
        {
            new ListItem(CutoffForm()) { Title = "Change cutoff timestamp", Subtitle = _cutoff.ToUniversalTime().ToString("O") },
            new ListItem(Confirmation(null)) { Title = "Mark all notifications read", Subtitle = "All repositories, including notifications hidden by local or API filters" },
        };
        items.AddRange(_repositories.Select(repository => new ListItem(Confirmation(repository))
        {
            Title = $"Mark {repository} notifications read",
            Subtitle = "All notifications in this repository, not just visible rows",
        }));
        if (_customRepository is { } custom && !_repositories.Contains(custom, StringComparer.OrdinalIgnoreCase))
        {
            items.Add(new ListItem(Confirmation(custom)) { Title = $"Mark {custom} notifications read", Subtitle = "All notifications in this repository, not just visible rows" });
        }
        return [.. items];
    }

    private BrowsingFormPage CutoffForm() => new("Bulk read cutoff",
        string.Join(',', BrowsingFormPage.Text("timestamp", "Last read at (ISO 8601 timestamp with timezone)", _cutoff.ToUniversalTime().ToString("O")),
            BrowsingFormPage.Text("repository", "Repository scope (optional owner/name)", _customRepository ?? "")),
        inputs =>
        {
            if (!_isCurrent() || _disposed) { return "Your account changed. Review a new bulk action."; }
            var cutoff = NotificationsPage.Timestamp(GitHubRest.GetString(inputs, "timestamp"))
                ?? throw new GitHubApiException("Enter the exact cutoff timestamp.");
            if (cutoff > DateTimeOffset.UtcNow) { return "Choose a cutoff at or before now."; }
            var repository = GitHubJson.Optional(GitHubRest.GetString(inputs, "repository"));
            if (repository is not null) { _ = NotificationsClient.QueryUri(_account, new(Repository: repository)); }
            _cutoff = cutoff;
            _customRepository = repository;
            RaiseItemsChanged();
            return null;
        });

    internal MutationConfirmationPage Confirmation(string? repository)
    {
        var cutoff = _cutoff;
        var scope = repository is null ? "All repositories for this account" : repository;
        return new(_account, "Mark notifications read", scope,
            $"Mark notifications updated at or before {cutoff.ToUniversalTime():O} read. This affects the entire scope, including notifications hidden by your filters.",
            () => SubmitAsync(repository, cutoff), () => (_message, _authorize), () => !_disposed && _isCurrent());
    }

    internal async Task SubmitAsync(string? repository, DateTimeOffset cutoff)
    {
        if (_disposed || !_isCurrent() || _client is null) { return; }
        async Task<MutationResult<bool>> Check(CancellationToken token)
        {
            Uri? next = null;
            for (var pageNumber = 0; pageNumber < 100; pageNumber++)
            {
                var page = await _client.GetNotificationsAsync(_account,
                    new(UnreadOnly: true, Repository: repository), next, null, token).ConfigureAwait(false);
                if (page.Notifications.Any(n => n.Unread && n.UpdatedAt <= cutoff))
                {
                    return new(MutationState.Pending, Error: "GitHub is still processing this scope. Refresh before retrying.");
                }

                next = page.NextPage;
                if (next is null) { return new(MutationState.Completed, true); }
            }

            return new(MutationState.Pending, Error: "This scope is too large to verify in one pass. Check GitHub before retrying.");
        }

        var result = await _executor.ExecuteAsync(_account, $"bulk-read:{repository ?? "*"}:{cutoff:O}",
            _ => Task.FromResult(!_disposed && _isCurrent()),
            async token =>
            {
                if (_disposed || !_isCurrent()) { return new MutationResult<bool>(MutationState.Stale); }
                var accepted = await _client.MarkAllReadAsync(_account, repository, cutoff, token).ConfigureAwait(false);
                if (accepted)
                {
                    return new MutationResult<bool>(MutationState.Pending, Error: "GitHub accepted the bulk request and is processing it. Refresh to check the inbox.");
                }

                return new MutationResult<bool>(MutationState.Completed, true);
            }, Check, _lifetime.Token).ConfigureAwait(false);
        if (_disposed || !_isCurrent()) { return; }
        _message = result.State == MutationState.Completed ? "GitHub confirmed the selected scope was marked read."
            : result.Error ?? "The outcome needs checking. Refresh before retrying.";
        _authorize = result.AuthorizeUrl;
        await _refresh().ConfigureAwait(false);
    }

    public void Dispose() { _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
