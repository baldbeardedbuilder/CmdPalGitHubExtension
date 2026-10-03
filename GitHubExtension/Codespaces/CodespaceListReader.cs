// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

internal static class CodespaceListReader
{
    internal static async Task<List<GitHubCodespace>> GetAllAsync(
        ICodespacesClient client,
        GitHubAccount account,
        CancellationToken cancellationToken)
    {
        var result = new List<GitHubCodespace>();
        var visited = new HashSet<Uri>();
        int? totalCount = null;
        Uri? page = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await client.GetCodespacesAsync(account, page, cancellationToken).ConfigureAwait(false);
            if (!batch.IsComplete || (batch.NextPage is { } next
                && (!next.IsAbsoluteUri || !visited.Add(next) || next.Scheme != account.Host.ApiUrl.Scheme
                    || next.Authority != account.Host.ApiUrl.Authority
                    || next.AbsolutePath != "/user/codespaces")))
            {
                throw new GitHubApiException("GitHub's Codespaces list was incomplete. Refresh to check again.");
            }

            if (batch.TotalCount is { } count)
            {
                if (totalCount is { } previous && previous != count)
                {
                    throw new GitHubApiException("GitHub's Codespaces list changed while checking. Refresh to check again.");
                }

                totalCount = count;
            }

            result.AddRange(batch.Codespaces);
            page = batch.NextPage;
        }
        while (page is not null);

        var unique = result.DistinctBy(codespace => codespace.Name, StringComparer.Ordinal).ToList();
        if (totalCount is { } expected && unique.Count != expected)
        {
            throw new GitHubApiException("GitHub's Codespaces list was incomplete. Refresh to check again.");
        }

        return unique;
    }
}
