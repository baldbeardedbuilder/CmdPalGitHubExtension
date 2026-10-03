// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal sealed record PullRequestActionSnapshot(
    SubjectState State,
    bool Merged,
    bool CanWrite,
    bool CanTriage,
    string[] RequestedReviewers,
    string[] RequestedTeams,
    string[] Assignees,
    string[] Labels);

internal sealed record PullRequestActionOptions(
    string[] Reviewers,
    string[] Teams,
    string[] Assignees,
    string[] Labels);

internal interface IPullRequestActionsClient
{
    Task<PullRequestActionSnapshot> GetSnapshotAsync(GitHubAccount account, string repository, int number, CancellationToken cancellationToken);

    Task<PullRequestActionOptions> GetOptionsAsync(GitHubAccount account, string repository, CancellationToken cancellationToken);

    Task SetStateAsync(GitHubAccount account, string repository, int number, bool open, CancellationToken cancellationToken);

    Task SetReviewerAsync(GitHubAccount account, string repository, int number, string reviewer, bool team, bool add, CancellationToken cancellationToken);

    Task SetAssigneeAsync(GitHubAccount account, string repository, int number, string assignee, bool add, CancellationToken cancellationToken);

    Task SetLabelAsync(GitHubAccount account, string repository, int number, string label, bool add, CancellationToken cancellationToken);
}

internal sealed class PullRequestActionsClient(HttpClient httpClient) : IPullRequestActionsClient
{
    private const int PageSize = 100;

    public Task<PullRequestActionSnapshot> GetSnapshotAsync(
        GitHubAccount account, string repository, int number, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.PullRequests, () => ReadSnapshotAsync(account, repository, number, cancellationToken),
            cancellationToken: cancellationToken);

    public Task<PullRequestActionOptions> GetOptionsAsync(
        GitHubAccount account, string repository, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.PullRequests, async () =>
    {
        var parts = RepositoryParts(repository);
        using var repoResponse = await SendAsync(httpClient, account, HttpMethod.Get, RepoUri(account, repository, null), cancellationToken)
            .ConfigureAwait(false);
        using var repoJson = await ReadJsonAsync(repoResponse, cancellationToken).ConfigureAwait(false);
        var ownerType = repoJson.RootElement.ValueKind == JsonValueKind.Object
            && repoJson.RootElement.TryGetProperty("owner", out var owner)
            && owner.ValueKind == JsonValueKind.Object ? GetString(owner, "type") : null;
        var collaborators = await ReadCollectionAsync(account, RepoUri(account, repository, "collaborators?per_page=100"), cancellationToken)
            .ConfigureAwait(false);
        var assignees = await ReadCollectionAsync(account, RepoUri(account, repository, "assignees?per_page=100"), cancellationToken)
            .ConfigureAwait(false);
        var labels = await ReadCollectionAsync(account, RepoUri(account, repository, "labels?per_page=100"), cancellationToken)
            .ConfigureAwait(false);
        var teams = ownerType == "Organization"
            ? await ReadCollectionAsync(account, new Uri(account.Host.ApiUrl,
                $"orgs/{Uri.EscapeDataString(parts.Owner)}/teams?per_page=100"), cancellationToken).ConfigureAwait(false)
            : [];
        return new PullRequestActionOptions(
            Names(collaborators, "login"),
            Teams(teams),
            Names(assignees, "login"),
            Names(labels, "name"));
    }, cancellationToken: cancellationToken);

    public async Task SetStateAsync(
        GitHubAccount account, string repository, int number, bool open, CancellationToken cancellationToken)
    {
        var target = await ReadSnapshotAsync(account, repository, number, cancellationToken).ConfigureAwait(false);
        EnsureWrite(target);
        EnsureStateCanChange(target, open);

        using var content = JsonContent(new PullRequestStateRequest(open ? "open" : "closed"));
        using var response = await SendAsync(httpClient, account, HttpMethod.Patch, PullUri(account, repository, number),
            cancellationToken, content: content).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var returned = ParseState(json.RootElement);
        if (returned.Merged || returned.State != (open ? SubjectState.Open : SubjectState.Closed))
        {
            throw new GitHubApiException("GitHub did not confirm the requested pull request state.");
        }
    }

    public async Task SetReviewerAsync(
        GitHubAccount account, string repository, int number, string reviewer, bool team, bool add, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new GitHubApiException("Choose a valid reviewer.");
        }

        var target = await ReadSnapshotAsync(account, repository, number, cancellationToken).ConfigureAwait(false);
        EnsureWrite(target);
        EnsureOpen(target);
        var current = team ? target.RequestedTeams : target.RequestedReviewers;
        EnsureMembership(current, reviewer, shouldExist: !add, "reviewer");
        using var content = JsonContent(team
            ? new PullRequestReviewersRequest([], [reviewer])
            : new PullRequestReviewersRequest([reviewer], []));
        var method = add ? HttpMethod.Post : HttpMethod.Delete;
        using var response = await SendAsync(httpClient, account, method, ReviewersUri(account, repository, number),
            cancellationToken, content: content).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var returnedReviewers = Names(json.RootElement, "requested_reviewers", "login");
        var returnedTeams = Names(json.RootElement, "requested_teams", "slug");
        var returned = team ? returnedTeams : returnedReviewers;
        EnsureMembership(returned, reviewer, add, "reviewer");
        foreach (var existing in target.RequestedReviewers.Where(name =>
            team || add || !string.Equals(name, reviewer, StringComparison.OrdinalIgnoreCase)))
        {
            EnsureMembership(returnedReviewers, existing, true, "reviewer");
        }

        foreach (var existing in target.RequestedTeams.Where(name =>
            !team || add || !string.Equals(name, reviewer, StringComparison.OrdinalIgnoreCase)))
        {
            EnsureMembership(returnedTeams, existing, true, "reviewer");
        }
    }

    public async Task SetAssigneeAsync(
        GitHubAccount account, string repository, int number, string assignee, bool add, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(assignee))
        {
            throw new GitHubApiException("Choose a valid assignee.");
        }

        var target = await ReadSnapshotAsync(account, repository, number, cancellationToken).ConfigureAwait(false);
        EnsureCanEditIssue(target);
        EnsureMembership(target.Assignees, assignee, shouldExist: !add, "assignee");
        using var content = JsonContent(new GitHubNamesRequest(Assignees: [assignee]));
        using var response = await SendAsync(httpClient, account, add ? HttpMethod.Post : HttpMethod.Delete,
            IssueUri(account, repository, number, "assignees"), cancellationToken, content: content).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var returned = Names(json.RootElement, "assignees", "login");
        EnsureMembership(returned, assignee, add, "assignee");
        foreach (var existing in target.Assignees.Where(name => !string.Equals(name, assignee, StringComparison.OrdinalIgnoreCase) || add))
        {
            EnsureMembership(returned, existing, true, "assignee");
        }
    }

    public async Task SetLabelAsync(
        GitHubAccount account, string repository, int number, string label, bool add, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new GitHubApiException("Choose a valid label.");
        }

        var target = await ReadSnapshotAsync(account, repository, number, cancellationToken).ConfigureAwait(false);
        EnsureCanEditIssue(target);
        EnsureMembership(target.Labels, label, shouldExist: !add, "label");
        if (add)
        {
            using var content = JsonContent(new GitHubNamesRequest(Labels: [label]));
            using var response = await SendAsync(httpClient, account, HttpMethod.Post,
                IssueUri(account, repository, number, "labels"), cancellationToken, content: content).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            EnsureMembership(Names(json.RootElement, "name"), label, true, "label");
            foreach (var existing in target.Labels)
            {
                EnsureMembership(Names(json.RootElement, "name"), existing, true, "label");
            }
        }
        else
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Delete,
                new Uri(IssueUri(account, repository, number, "labels").AbsoluteUri + "/" + Uri.EscapeDataString(label)),
                cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var removed = GetString(json.RootElement, "name");
            if (!string.Equals(removed, label, StringComparison.Ordinal))
            {
                throw new GitHubApiException("GitHub did not confirm the requested label removal.");
            }
        }
    }

    private async Task<PullRequestActionSnapshot> ReadSnapshotAsync(
        GitHubAccount account, string repository, int number, CancellationToken cancellationToken)
    {
        RepositoryParts(repository);
        using var repoResponse = await SendAsync(httpClient, account, HttpMethod.Get, RepoUri(account, repository, null), cancellationToken)
            .ConfigureAwait(false);
        using var repoJson = await ReadJsonAsync(repoResponse, cancellationToken).ConfigureAwait(false);
        var permissions = repoJson.RootElement.ValueKind == JsonValueKind.Object
            && repoJson.RootElement.TryGetProperty("permissions", out var value) && value.ValueKind == JsonValueKind.Object ? value : default;
        var canWrite = GetBool(permissions, "push") || GetBool(permissions, "maintain") || GetBool(permissions, "admin");
        var canTriage = canWrite || GetBool(permissions, "triage");
        using var pullResponse = await SendAsync(httpClient, account, HttpMethod.Get, PullUri(account, repository, number), cancellationToken)
            .ConfigureAwait(false);
        using var pullJson = await ReadJsonAsync(pullResponse, cancellationToken).ConfigureAwait(false);
        var state = ParseState(pullJson.RootElement);
        return new PullRequestActionSnapshot(
            state.State,
            state.Merged,
            canWrite,
            canTriage,
            Names(pullJson.RootElement, "requested_reviewers", "login"),
            Names(pullJson.RootElement, "requested_teams", "slug"),
            Names(pullJson.RootElement, "assignees", "login"),
            Names(pullJson.RootElement, "labels", "name"));
    }

    private async Task<JsonElement[]> ReadCollectionAsync(GitHubAccount account, Uri uri, CancellationToken cancellationToken)
    {
        var all = new List<JsonElement>();
        Uri? page = uri;
        while (page is not null)
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, page, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException("GitHub returned a list we couldn't read.");
            }

            all.AddRange(json.RootElement.EnumerateArray().Select(item => item.Clone()));
            page = NextPage(response);
        }

        return [.. all];
    }

    private static (SubjectState State, bool Merged) ParseState(JsonElement pull)
    {
        if (pull.ValueKind != JsonValueKind.Object)
        {
            throw new GitHubApiException("GitHub returned a pull request state we couldn't verify.");
        }

        var merged = GetBool(pull, "merged") || GetString(pull, "merged_at") is not null;
        var state = GetString(pull, "state") switch
        {
            "open" => SubjectState.Open,
            "closed" when merged => SubjectState.Merged,
            "closed" => SubjectState.Closed,
            _ => SubjectState.Unknown,
        };
        if (state == SubjectState.Unknown)
        {
            throw new GitHubApiException("GitHub returned a pull request state we couldn't verify.");
        }

        return (state, merged);
    }

    private static string[] Names(JsonElement root, string property, string nameProperty)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return Names(values, nameProperty);
    }

    private static string[] Names(JsonElement array, string nameProperty)
    {
        if (array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => GetString(item, nameProperty))
            .OfType<string>()
            .ToArray();
    }

    private static string[] Names(JsonElement[] values, string nameProperty) =>
        values.Select(value => GetString(value, nameProperty)).OfType<string>().ToArray();

    private static string[] Teams(JsonElement[] teams) =>
        teams.Select(team => GetString(team, "slug")).OfType<string>().ToArray();

    private static void EnsureWrite(PullRequestActionSnapshot snapshot)
    {
        if (!snapshot.CanWrite)
        {
            throw new GitHubApiException("Write access to this repository is required for pull request actions.");
        }
    }

    private static void EnsureCanEditIssue(PullRequestActionSnapshot snapshot)
    {
        if (!snapshot.CanTriage)
        {
            throw new GitHubApiException("Triage or write access to this repository is required to edit assignees or labels.");
        }
    }

    private static void EnsureOpen(PullRequestActionSnapshot snapshot)
    {
        if (snapshot.Merged || snapshot.State != SubjectState.Open)
        {
            throw new GitHubApiException("Only open, unmerged pull requests can change review requests.");
        }
    }

    private static void EnsureStateCanChange(PullRequestActionSnapshot snapshot, bool open)
    {
        if (snapshot.Merged)
        {
            throw new GitHubApiException("Merged pull requests cannot be reopened or closed.");
        }

        if (open ? snapshot.State != SubjectState.Closed : snapshot.State != SubjectState.Open)
        {
            throw new GitHubApiException("The pull request state changed. Refresh and review the action again.");
        }
    }

    private static void EnsureMembership(string[] values, string value, bool shouldExist, string kind)
    {
        var exists = values.Contains(value, StringComparer.OrdinalIgnoreCase);
        if (exists != shouldExist)
        {
            throw new GitHubApiException($"GitHub did not confirm the requested {kind} change.");
        }
    }

    private static StringContent JsonContent(PullRequestStateRequest payload) =>
        new(JsonSerializer.Serialize(payload, GitHubJsonContext.Default.PullRequestStateRequest), Encoding.UTF8, "application/json");

    private static StringContent JsonContent(PullRequestReviewersRequest payload) =>
        new(JsonSerializer.Serialize(payload, GitHubJsonContext.Default.PullRequestReviewersRequest), Encoding.UTF8, "application/json");

    private static StringContent JsonContent(GitHubNamesRequest payload) =>
        new(JsonSerializer.Serialize(payload, GitHubJsonContext.Default.GitHubNamesRequest), Encoding.UTF8, "application/json");

    private static Uri RepoUri(GitHubAccount account, string repository, string? endpoint)
    {
        var parts = RepositoryParts(repository);
        var suffix = endpoint is null ? string.Empty : "/" + endpoint;
        return new Uri(account.Host.ApiUrl,
            $"repos/{Uri.EscapeDataString(parts.Owner)}/{Uri.EscapeDataString(parts.Name)}{suffix}");
    }

    private static Uri PullUri(GitHubAccount account, string repository, int number) =>
        number > 0 ? RepoUri(account, repository, $"pulls/{number}") : throw new GitHubApiException("A valid pull request number is required.");

    private static Uri ReviewersUri(GitHubAccount account, string repository, int number) =>
        new(PullUri(account, repository, number).AbsoluteUri + "/requested_reviewers");

    private static Uri IssueUri(GitHubAccount account, string repository, int number, string endpoint) =>
        number > 0 ? RepoUri(account, repository, $"issues/{number}/{endpoint}") : throw new GitHubApiException("A valid pull request number is required.");

    private static (string Owner, string Name) RepositoryParts(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || parts.Any(part => part is "." or ".."))
        {
            throw new GitHubApiException("The repository name must be in owner/name format.");
        }

        return (parts[0], parts[1]);
    }
}
