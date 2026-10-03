using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class NotificationsPage
{
    private NotificationQuery _query = new();
    private DateTimeOffset? _lastModified;
    private DateTimeOffset _nextPoll;
    private bool _forceRefresh;
    private IContextItem[]? _browsingCommands;
    private CancellationTokenSource _pollLifetime = new();

    internal IContextItem[] NativeDetailCommands(NotificationItem item)
    {
        if (item.Account is not { } account || item.Notification.SubjectType is not ("Issue" or "PullRequest")
            || item.Notification.SubjectApiUrl is not { } api
            || !int.TryParse(api.Segments.LastOrDefault()?.Trim('/'), out var number) || number <= 0)
        {
            return [];
        }

        return _nativeDetails.Commands($"{item.AccountGeneration}:{item.Notification.Id}", account,
            item.Notification.RepositoryFullName, number, item.Notification.SubjectType == "PullRequest",
            () => Volatile.Read(ref _accountGeneration) == item.AccountGeneration);
    }

    internal IContextItem[] BrowsingCommands()
    {
        if (_client is not INotificationBrowsingClient || CurrentAccount is null) { return []; }
        return _browsingCommands ??= [new CommandContextItem(QueryForm()), new CommandContextItem(BulkReadPage())];
    }

    internal BrowsingFormPage QueryForm()
    {
        var account = CurrentAccount;
        var generation = AccountGeneration;
        var query = _query;
        return new("Filter notification API results", string.Join(',',
            BrowsingFormPage.Choice("unread", "Read status", query.UnreadOnly ? "true" : "false", ("All notifications", "false"), ("Unread only", "true")),
            BrowsingFormPage.Choice("participating", "Participation", query.Participating ? "true" : "false", ("All reasons", "false"), ("Participating only", "true")),
            BrowsingFormPage.Text("repository", "Repository (optional owner/name)", query.Repository ?? ""),
            BrowsingFormPage.Text("since", "Since (optional ISO 8601 timestamp)", query.Since?.ToString("O") ?? ""),
            BrowsingFormPage.Text("before", "Before (optional ISO 8601 timestamp)", query.Before?.ToString("O") ?? "")), inputs =>
            {
                if (account is null || CurrentAccount != account || AccountGeneration != generation || _load.Disposed)
                {
                    return "Your account changed. Open the filter again.";
                }

                var next = new NotificationQuery(GitHubRest.GetString(inputs, "unread") == "true",
                    GitHubRest.GetString(inputs, "participating") == "true",
                    GitHubJson.Optional(GitHubRest.GetString(inputs, "repository")),
                    Timestamp(GitHubRest.GetString(inputs, "since")), Timestamp(GitHubRest.GetString(inputs, "before")));
                _ = NotificationsClient.QueryUri(account, next);
                SetQuery(next);
                return null;
            });
    }

    internal Task SetQuery(NotificationQuery query)
    {
        lock (_lock)
        {
            if (_load.Disposed) { return Task.CompletedTask; }
            _load.Invalidate(reset: true);
            Interlocked.Increment(ref _accountGeneration);
            _query = query;
            _browsingCommands = null;
            _pollLifetime.Cancel();
            _pollLifetime.Dispose();
            _pollLifetime = new();
            _lastModified = null;
            _nextPoll = default;
            _items.Clear();
            _mutationError = null;
        }

        _nativeDetails.Dispose();
        HasMoreItems = false;
        RaiseItemsChanged();
        return StartLoad(true);
    }

    internal static DateTimeOffset? Timestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!TimestampPattern().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            throw new GitHubApiException("Enter an ISO 8601 timestamp with a timezone.");
        }

        return value;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")]
    private static partial System.Text.RegularExpressions.Regex TimestampPattern();

    internal BulkNotificationReadPage BulkReadPage()
    {
        var account = CurrentAccount;
        var generation = AccountGeneration;
        string[] repositories;
        lock (_lock) { repositories = _items.Select(i => i.Notification.RepositoryFullName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
        return new(account!, _client as INotificationBrowsingClient, _executor, _time.GetUtcNow(), repositories,
            () => !_load.Disposed && account is not null && CurrentAccount == account && AccountGeneration == generation,
            RefreshAfterMutationAsync);
    }

    internal Task RefreshAfterMutationAsync()
    {
        TimeSpan delay;
        CancellationToken token;
        lock (_lock)
        {
            if (_load.Disposed) { return Task.CompletedTask; }
            _forceRefresh = true;
            _lastModified = null;
            delay = _nextPoll - _time.GetUtcNow();
            token = _pollLifetime.Token;
        }

        if (delay <= TimeSpan.Zero) { return RefreshAsync(); }
        _ = DeferredRefreshAsync(delay, token);
        return Task.CompletedTask;
    }

    private async Task DeferredRefreshAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            while (delay > TimeSpan.Zero)
            {
                var step = delay > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : delay;
                await Task.Delay(step, _time, token).ConfigureAwait(false);
                delay -= step;
            }

            if (!token.IsCancellationRequested) { await RefreshAsync().ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
