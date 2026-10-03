// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal enum IssueChangeKind { State, Assignee, Label }

internal sealed record IssueChange(IssueChangeKind Kind, string? Selection = null, bool Add = true, SubjectState State = SubjectState.Unknown)
{
    internal string Title => Kind switch
    {
        IssueChangeKind.State => State switch
        {
            SubjectState.Open => "Reopen issue",
            SubjectState.Closed => "Close as completed",
            _ => "Close as not planned",
        },
        IssueChangeKind.Assignee => $"{(Add ? "Assign" : "Remove assignee")} @{Selection}",
        _ => $"{(Add ? "Add" : "Remove")} label: {Selection}",
    };

    internal bool Contains(GitHubIssue issue) => Kind == IssueChangeKind.Assignee
        ? issue.Assignees.Contains(Selection!, StringComparer.OrdinalIgnoreCase)
        : issue.Labels.Contains(Selection!, StringComparer.OrdinalIgnoreCase);

    internal bool Satisfied(GitHubIssue issue) => Kind == IssueChangeKind.State ? issue.State == State : Contains(issue) == Add;
    internal bool Allowed(GitHubIssue issue) => Kind == IssueChangeKind.State
        ? State == SubjectState.Open
            ? issue.State is SubjectState.Closed or SubjectState.NotPlanned
            : State is SubjectState.Closed or SubjectState.NotPlanned && issue.State == SubjectState.Open
        : Kind is IssueChangeKind.Assignee or IssueChangeKind.Label && !string.IsNullOrWhiteSpace(Selection) && Contains(issue) != Add;
}

internal sealed partial class IssueMutationSession : IDisposable
{
    private readonly IIssueMutationsClient _client;
    private readonly MutationExecutor _executor;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, IssueChange> _pending = [];

    internal IssueMutationSession(AuthService auth, IIssueMutationsClient client)
    {
        _client = client;
        _executor = new MutationExecutor(auth);
    }

    internal async Task<MutationResult<GitHubIssue>> ExecuteAsync(
        GitHubAccount account, string repository, GitHubIssue reviewed, IssueChange change,
        Func<bool> isCurrent, CancellationToken token)
    {
        var key = $"issue:{account.Host.ApiUrl}:{account.Login.ToLowerInvariant()}:{repository.ToLowerInvariant()}#{reviewed.Number}";
        GitHubIssue? before = null;
        using var diagnostics = OperationDiagnostics.Begin(DiagnosticEvent.Mutation, DiagnosticArea.Issues);
        var result = await _executor.ExecuteAsync<GitHubIssue>(account, key, async cancellation =>
        {
            if (!isCurrent() || !change.Allowed(reviewed))
            {
                return false;
            }

            before = await _client.GetMutationIssueAsync(account, repository, reviewed.Number, cancellation).ConfigureAwait(false);
            if (before.Number != reviewed.Number || before.WebUrl != reviewed.WebUrl || !change.Allowed(before)
                || (change.Kind == IssueChangeKind.State && before.State != reviewed.State))
            {
                return false;
            }

            if (change.Add && change.Kind != IssueChangeKind.State)
            {
                var available = await ChoicesAsync(account, repository, change.Kind, cancellation).ConfigureAwait(false);
                if (!available.Contains(change.Selection!, StringComparer.OrdinalIgnoreCase))
                {
                    throw new GitHubApiException("That selection is no longer available in this repository. Refresh and choose again.");
                }
            }

            return isCurrent();
        }, async cancellation =>
        {
            lock (_lock)
            {
                _pending[key] = change;
            }

            GitHubIssue updated;
            switch (change.Kind)
            {
                case IssueChangeKind.State:
                    updated = await _client.ChangeStateAsync(account, repository, reviewed.Number, change.State, cancellation).ConfigureAwait(false);
                    break;
                case IssueChangeKind.Assignee:
                    updated = await _client.ChangeAssigneeAsync(account, repository, reviewed.Number, change.Selection!, change.Add, cancellation).ConfigureAwait(false);
                    break;
                default:
                    var labels = await _client.ChangeLabelAsync(account, repository, reviewed.Number, change.Selection!, change.Add, cancellation).ConfigureAwait(false);
                    if (!Preserved(before!.Labels, labels, change.Selection!, change.Add))
                    {
                        return new(MutationState.Unknown, Error: "GitHub didn't confirm the label change or unrelated labels. Refresh to check before retrying.");
                    }

                    updated = await ReadAfterWriteAsync(account, repository, reviewed.Number, cancellation).ConfigureAwait(false);
                    break;
            }

            if (updated.Number != reviewed.Number || updated.WebUrl != reviewed.WebUrl || !change.Satisfied(updated)
                || (change.Kind == IssueChangeKind.Assignee && !Preserved(before!.Assignees, updated.Assignees, change.Selection!, change.Add))
                || (change.Kind == IssueChangeKind.Label && !Preserved(before!.Labels, updated.Labels, change.Selection!, change.Add)))
            {
                return new(MutationState.Unknown, Error: "GitHub didn't confirm the requested change. Refresh to check before retrying.");
            }

            // Read once more after a write so the detail view reflects authoritative metadata.
            var fresh = await ReadAfterWriteAsync(account, repository, reviewed.Number, cancellation).ConfigureAwait(false);
            return fresh.Number == reviewed.Number && fresh.WebUrl == reviewed.WebUrl && change.Satisfied(fresh)
                && (change.Kind != IssueChangeKind.Assignee || Preserved(before!.Assignees, fresh.Assignees, change.Selection!, change.Add))
                && (change.Kind != IssueChangeKind.Label || Preserved(before!.Labels, fresh.Labels, change.Selection!, change.Add))
                ? new(MutationState.Completed, fresh)
                : new(MutationState.Unknown, Error: "The issue changed again before we could verify it. Refresh to check GitHub.");
        }, async cancellation =>
        {
            IssueChange? pending;
            lock (_lock)
            {
                _pending.TryGetValue(key, out pending);
            }

            if (pending is null || !isCurrent())
            {
                return new(MutationState.Unknown, Error: "The previous request may have succeeded. Check GitHub before retrying.");
            }

            var fresh = await _client.GetMutationIssueAsync(account, repository, reviewed.Number, cancellation).ConfigureAwait(false);
            if (fresh.Number != reviewed.Number || fresh.WebUrl != reviewed.WebUrl || !pending.Satisfied(fresh))
            {
                return new(MutationState.Unknown, Error: "The previous change is still unconfirmed. Check GitHub before retrying.");
            }

            return pending == change ? new(MutationState.Completed, fresh) : new(MutationState.RetryAllowed);
        }, token).ConfigureAwait(false);
        diagnostics.Complete(result.State switch
        {
            MutationState.Completed => DiagnosticOutcome.Completed,
            MutationState.Stale => DiagnosticOutcome.Cancelled,
            MutationState.Unknown => DiagnosticOutcome.Unknown,
            MutationState.Pending => DiagnosticOutcome.Accepted,
            _ => DiagnosticOutcome.Failed,
        });
        return result;
    }

    internal async Task<IReadOnlyList<string>> ChoicesAsync(
        GitHubAccount account, string repository, IssueChangeKind kind, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Uri>();
        Uri? page = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var result = kind == IssueChangeKind.Assignee
                ? await _client.GetAssigneesAsync(account, repository, page, token).ConfigureAwait(false)
                : await _client.GetLabelsAsync(account, repository, page, token).ConfigureAwait(false);
            if (result.Names.Any(string.IsNullOrWhiteSpace))
            {
                throw new GitHubApiException("GitHub sent back a picker selection we couldn't read.");
            }

            names.UnionWith(result.Names);
            page = result.NextPage;
            if (page is not null && !seen.Add(page))
            {
                throw new GitHubApiException("GitHub repeated a picker page. Refresh before choosing again.");
            }
        }
        while (page is not null);

        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool Preserved(IReadOnlyList<string> before, IReadOnlyList<string> after, string selection, bool add) =>
        after.Contains(selection, StringComparer.OrdinalIgnoreCase) == add
        && before.Where(name => !string.Equals(name, selection, StringComparison.OrdinalIgnoreCase))
            .All(name => after.Contains(name, StringComparer.OrdinalIgnoreCase));

    private async Task<GitHubIssue> ReadAfterWriteAsync(GitHubAccount account, string repository, int number, CancellationToken token)
    {
        try
        {
            return await _client.GetMutationIssueAsync(account, repository, number, token).ConfigureAwait(false);
        }
        catch (GitHubApiException ex)
        {
            var error = new GitHubApiException("GitHub received the change, but we couldn't verify it. Refresh to check before retrying.",
                ex, ex.AuthorizeUrl, outcomeUnknown: true);
            OperationDiagnostics.CorrelateFailure(ex, error);
            throw error;
        }
    }

    public void Dispose() => _executor.Dispose();
}
