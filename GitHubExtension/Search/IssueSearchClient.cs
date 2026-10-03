using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Search;

internal enum IssueSearchKind { All, Issues, PullRequests }
internal sealed record IssueSearchResult(long Id, int Number, string Title, Uri WebUrl, string Repository,
    bool IsPullRequest, string State, string? Author, string? Body);
internal sealed record IssueSearchPageResult(IReadOnlyList<IssueSearchResult> Items, Uri? NextPage, int Total, bool Incomplete);

internal interface IIssueSearchClient
{
    Task<IssueSearchPageResult> SearchAsync(GitHubAccount account, string query, IssueSearchKind kind, Uri? page, CancellationToken token);
}

internal sealed class IssueSearchClient(HttpClient http) : IIssueSearchClient
{
    internal const int PageSize = 30;
    internal const int ResultLimit = 1000;

    public Task<IssueSearchPageResult> SearchAsync(GitHubAccount account, string query, IssueSearchKind kind, Uri? page, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Issues, async () =>
        {
            var scoped = ScopeQuery(query, kind);
            var first = new Uri(account.Host.ApiUrl, $"search/issues?q={Uri.EscapeDataString(scoped)}&per_page={PageSize}");
            var uri = page ?? first;
            var number = PageNumber(uri, first, scoped);
            using var response = await SendAsync(http, account, HttpMethod.Get, uri, token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array || !root.TryGetProperty("total_count", out var total)
                || !total.TryGetInt32(out var count) || count < items.GetArrayLength())
            {
                throw new GitHubApiException("GitHub sent back search results we couldn't read.");
            }

            var parsed = items.EnumerateArray().Select(element =>
            {
                var url = GetUri(element, "html_url");
                var repositoryUrl = GetUri(element, "repository_url");
                if (url is null || url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo)
                    || !url.Authority.Equals(account.Host.WebUrl.Authority, StringComparison.OrdinalIgnoreCase)
                    || repositoryUrl is null || !element.TryGetProperty("id", out var id) || !id.TryGetInt64(out var identifier)
                    || identifier <= 0 || GetInt(element, "number") <= 0 || GetString(element, "title") is not { } title)
                {
                    throw new GitHubApiException("GitHub sent back a search result we couldn't read.");
                }

                if (repositoryUrl.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(repositoryUrl.UserInfo)
                    || !repositoryUrl.Authority.Equals(account.Host.ApiUrl.Authority, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GitHubApiException("GitHub returned an unsafe repository link.");
                }
                var parts = repositoryUrl.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var isPull = element.TryGetProperty("pull_request", out _);
                if (parts.Length < 3 || parts[^3] != "repos" || kind == IssueSearchKind.Issues && isPull
                    || kind == IssueSearchKind.PullRequests && !isPull)
                {
                    throw new GitHubApiException("GitHub returned results outside the selected search scope.");
                }

                return new IssueSearchResult(identifier, GetInt(element, "number"), title, url,
                    $"{Uri.UnescapeDataString(parts[^2])}/{Uri.UnescapeDataString(parts[^1])}",
                    isPull, GetString(element, "state") ?? "unknown",
                    element.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object ? GetString(user, "login") : null,
                    GetString(element, "body"));
            }).ToArray();
            var available = ResultLimit - (number - 1) * PageSize;
            var next = available <= PageSize ? null : NextPage(response);
            if (next is not null && PageNumber(next, first, scoped) != number + 1)
            {
                throw new GitHubApiException("GitHub sent back a search page outside the selected query.");
            }

            return new IssueSearchPageResult(parsed.Take(available).ToArray(), next, count,
                root.TryGetProperty("incomplete_results", out var incomplete) && incomplete.ValueKind == JsonValueKind.True);
        }, name: DiagnosticEvent.PageSearch, cancellationToken: token);

    internal static string ScopeQuery(string query, IssueSearchKind kind)
    {
        if (string.IsNullOrWhiteSpace(query)) { throw new GitHubApiException("Enter a GitHub search query."); }
        if (!Enum.IsDefined(kind)) { throw new GitHubApiException("Choose issues, pull requests, or both."); }
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var issue = terms.Any(t => t.Equals("is:issue", StringComparison.OrdinalIgnoreCase) || t.Equals("type:issue", StringComparison.OrdinalIgnoreCase));
        var pull = terms.Any(t => t.Equals("is:pr", StringComparison.OrdinalIgnoreCase) || t.Equals("type:pr", StringComparison.OrdinalIgnoreCase));
        if (kind == IssueSearchKind.Issues && pull || kind == IssueSearchKind.PullRequests && issue)
        {
            throw new GitHubApiException("Your query qualifier conflicts with the selected result type.");
        }

        return query.Trim() + (kind == IssueSearchKind.Issues && !issue ? " is:issue"
            : kind == IssueSearchKind.PullRequests && !pull ? " is:pr" : "");
    }

    internal static int PageNumber(Uri uri, Uri first, string query)
    {
        var values = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var page = values["page"] is null ? 1 : int.TryParse(values["page"], out var n) ? n : 0;
        if (uri.GetLeftPart(UriPartial.Path) != first.GetLeftPart(UriPartial.Path)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
            || values["q"] != query || values["per_page"] != PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || values.AllKeys.Any(k => k is not ("q" or "per_page" or "page"))
            || page <= 0 || (page - 1L) * PageSize >= ResultLimit)
        {
            throw new GitHubApiException("GitHub returned a search page outside the selected query or result limit.");
        }

        return page;
    }
}
