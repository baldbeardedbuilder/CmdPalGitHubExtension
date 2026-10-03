using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Search;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal sealed partial class RepositoriesClient : IIssueSearchClient
{
    public Task<IssueSearchPageResult> SearchAsync(GitHubAccount account, string query, IssueSearchKind kind, Uri? page, CancellationToken token) =>
        new IssueSearchClient(httpClient).SearchAsync(account, query, kind, page, token);
}
