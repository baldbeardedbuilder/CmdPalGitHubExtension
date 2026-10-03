// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal interface IIssueMutationsClient
{
    Task<GitHubIssue> GetMutationIssueAsync(GitHubAccount account, string repository, int number, CancellationToken token);
    Task<IssueChoicesResult> GetAssigneesAsync(GitHubAccount account, string repository, Uri? page, CancellationToken token);
    Task<IssueChoicesResult> GetLabelsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken token);
    Task<GitHubIssue> ChangeStateAsync(GitHubAccount account, string repository, int number, SubjectState state, CancellationToken token);
    Task<GitHubIssue> ChangeAssigneeAsync(GitHubAccount account, string repository, int number, string login, bool add, CancellationToken token);
    Task<IReadOnlyList<string>> ChangeLabelAsync(GitHubAccount account, string repository, int number, string name, bool add, CancellationToken token);
}

internal sealed record IssueChoicesResult(IReadOnlyList<string> Names, Uri? NextPage);

internal sealed partial class IssuesClient
{
    public Task<GitHubIssue> GetMutationIssueAsync(GitHubAccount account, string repository, int number, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, IssueUri(account, repository, number), token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            return ReadMutationIssue(json.RootElement, account, repository, number);
        }, cancellationToken: token);

    public Task<IssueChoicesResult> GetAssigneesAsync(GitHubAccount account, string repository, Uri? page, CancellationToken token) =>
        GetChoicesAsync(account, repository, "assignees", "login", page, token);

    public Task<IssueChoicesResult> GetLabelsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken token) =>
        GetChoicesAsync(account, repository, "labels", "name", page, token);

    private Task<IssueChoicesResult> GetChoicesAsync(
        GitHubAccount account, string repository, string resource, string field, Uri? page, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
        {
            var endpoint = new Uri(account.Host.ApiUrl, $"{RepositoryRoute(repository)}/{resource}");
            var uri = page ?? new Uri(endpoint.AbsoluteUri + $"?per_page={PageSize}");
            RequirePage(endpoint, uri);
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var names = ReadNames(json.RootElement, field);
            var next = NextPage(response);
            if (next is not null)
            {
                RequirePage(endpoint, next);
            }

            return new IssueChoicesResult(names, next);
        }, cancellationToken: token);

    public Task<GitHubIssue> ChangeStateAsync(GitHubAccount account, string repository, int number, SubjectState state, CancellationToken token)
    {
        var body = state switch
        {
            SubjectState.Open => """{"state":"open","state_reason":"reopened"}""",
            SubjectState.Closed => """{"state":"closed","state_reason":"completed"}""",
            SubjectState.NotPlanned => """{"state":"closed","state_reason":"not_planned"}""",
            _ => throw new GitHubApiException("Choose a supported issue state."),
        };
        return WriteIssueAsync(account, repository, number, HttpMethod.Patch, null, body, token);
    }

    public Task<GitHubIssue> ChangeAssigneeAsync(
        GitHubAccount account, string repository, int number, string login, bool add, CancellationToken token)
    {
        RequireSelection(login);
        return WriteIssueAsync(account, repository, number, add ? HttpMethod.Post : HttpMethod.Delete, "assignees",
            $$"""{"assignees":[{{GitHubJson.String(login)}}]}""", token);
    }

    public Task<IReadOnlyList<string>> ChangeLabelAsync(
        GitHubAccount account, string repository, int number, string name, bool add, CancellationToken token) =>
        DomainDiagnostics.RunAsync<IReadOnlyList<string>>(DiagnosticArea.Issues, async () =>
        {
            RequireSelection(name);
            var uri = add ? IssueUri(account, repository, number, "labels") : LabelUri(account, repository, number, name);
            using var content = add ? JsonContent($$"""{"labels":[{{GitHubJson.String(name)}}]}""") : null;
            using var response = await SendMutationAsync(httpClient, account, add ? HttpMethod.Post : HttpMethod.Delete, uri, token, content: content).ConfigureAwait(false);
            if (response.IsAccepted || response.Json is null)
            {
                throw new GitHubApiException("GitHub hasn't confirmed the labels. Refresh to check before retrying.", outcomeUnknown: true);
            }

            return ReadNames(response.Json.RootElement, "name", mutation: true);
        }, cancellationToken: token);

    private Task<GitHubIssue> WriteIssueAsync(
        GitHubAccount account, string repository, int number, HttpMethod method, string? suffix, string body, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
        {
            using var content = JsonContent(body);
            using var response = await SendMutationAsync(httpClient, account, method, IssueUri(account, repository, number, suffix), token, content: content).ConfigureAwait(false);
            if (response.IsAccepted || response.Json is null)
            {
                throw new GitHubApiException("GitHub hasn't confirmed the issue change. Refresh to check before retrying.", outcomeUnknown: true);
            }

            return ReadMutationIssue(response.Json.RootElement, account, repository, number, mutation: true);
        }, cancellationToken: token);

    private static StringContent JsonContent(string body) => new(body, Encoding.UTF8, "application/json");

    internal static Uri IssueUri(GitHubAccount account, string repository, int number, string? suffix = null)
    {
        if (number <= 0)
        {
            throw new GitHubApiException("Choose a valid issue number.");
        }

        return new Uri(account.Host.ApiUrl, $"{RepositoryRoute(repository)}/issues/{number.ToString(CultureInfo.InvariantCulture)}{(suffix is null ? string.Empty : "/" + suffix)}");
    }

    private static string RepositoryRoute(string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(part => string.IsNullOrWhiteSpace(part)
            || part is "." or ".." || part.Any(char.IsWhiteSpace) || part.Contains('\\')))
        {
            throw new GitHubApiException("The repository name must be in owner/name format.");
        }

        return $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
    }

    private static void RequirePage(Uri endpoint, Uri page)
    {
        if (!page.IsAbsoluteUri || page.Scheme != Uri.UriSchemeHttps || page.Authority != endpoint.Authority
            || page.AbsolutePath != endpoint.AbsolutePath || page.UserInfo.Length != 0 || page.Fragment.Length != 0)
        {
            throw new GitHubApiException("GitHub sent back an unexpected picker page.");
        }
    }

    private static void RequireSelection(string selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            throw new GitHubApiException("Choose an existing repository assignee or label.");
        }
    }

    private static Uri LabelUri(GitHubAccount account, string repository, int number, string name)
    {
        var endpoint = IssueUri(account, repository, number, "labels").AbsoluteUri + "/";
        // Dot-only labels need escaped bytes without URI path normalization.
        if (name is "." or "..")
        {
            return new Uri(endpoint + (name == "." ? "%2E" : "%2E%2E"),
                new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        }

        return new Uri(endpoint + Uri.EscapeDataString(name));
    }

    private static List<string> ReadNames(JsonElement array, string field, bool mutation = false) =>
        DomainDiagnostics.Read(DiagnosticArea.Issues, () =>
        {
            if (array.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException("GitHub sent back metadata we couldn't read.", outcomeUnknown: mutation);
            }

            var names = new List<string>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(GetString(item, field)))
                {
                    throw new GitHubApiException("GitHub sent back metadata we couldn't read.", outcomeUnknown: mutation);
                }

                names.Add(GetString(item, field)!);
            }

            return names;
        });

    private static GitHubIssue ReadMutationIssue(
        JsonElement element, GitHubAccount account, string repository, int number, bool mutation = false) =>
        DomainDiagnostics.Read(DiagnosticArea.Issues, () =>
        {
            if (element.ValueKind != JsonValueKind.Object || element.TryGetProperty("pull_request", out _)
                || GetInt(element, "number") != number || GetString(element, "state") is not ("open" or "closed")
                || !element.TryGetProperty("assignees", out var assignees) || !element.TryGetProperty("labels", out var labels)
                || GetUri(element, "html_url") != new Uri(account.Host.WebUrl, $"{repository}/issues/{number.ToString(CultureInfo.InvariantCulture)}")
                || (GetString(element, "state") == "closed" && (GetString(element, "state_reason") is not (null or "completed" or "not_planned")
                    || mutation && GetString(element, "state_reason") is null)))
            {
                throw new GitHubApiException("GitHub sent back an issue we couldn't verify.", outcomeUnknown: mutation);
            }

            _ = ReadNames(assignees, "login", mutation);
            _ = ReadNames(labels, "name", mutation);
            return ParseIssue(element);
        });
}
