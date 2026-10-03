// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal interface IRepositoryStarsClient
{
    Task<bool> IsStarredAsync(GitHubAccount account, string repository, CancellationToken cancellationToken);
    Task SetStarredAsync(GitHubAccount account, string repository, bool starred, CancellationToken cancellationToken);
    Task<RepositoriesPageResult> GetStarredAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken);
}

internal sealed partial class RepositoriesClient
{
    public Task<bool> IsStarredAsync(GitHubAccount account, string repository, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Repositories, async () =>
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, StarUri(account, repository),
                cancellationToken, throwOnError: false).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => true,
                HttpStatusCode.NotFound => false,
                _ => throw SsoRequired(account, response) ?? StarStateError(response),
            };
        }, cancellationToken: cancellationToken);

    public async Task SetStarredAsync(GitHubAccount account, string repository, bool starred, CancellationToken cancellationToken)
    {
        var sent = false;
        await DomainDiagnostics.RunAsync(DiagnosticArea.Repositories, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            using var response = await SendMutationAsync(httpClient, account, starred ? HttpMethod.Put : HttpMethod.Delete,
                StarUri(account, repository), cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.NoContent ? DiagnosticOutcome.Completed : DiagnosticOutcome.Accepted;
        }, DiagnosticEvent.Mutation, outcome => outcome, () => sent, cancellationToken).ConfigureAwait(false);
    }

    public Task<RepositoriesPageResult> GetStarredAsync(GitHubAccount account, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Repositories, async () =>
        {
            var first = new Uri(account.Host.ApiUrl, $"user/starred?sort=created&direction=desc&per_page={PageSize}");
            if (page is not null && (page.GetLeftPart(UriPartial.Path) != first.GetLeftPart(UriPartial.Path)
                || !string.IsNullOrEmpty(page.UserInfo) || !string.IsNullOrEmpty(page.Fragment)))
            {
                throw new GitHubApiException("GitHub sent back a starred repository page we couldn't read. Try refreshing.");
            }

            using var response = await SendAsync(httpClient, account, HttpMethod.Get, page ?? first, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new RepositoriesPageResult(ParseRepositories(json.RootElement), NextPage(response));
        }, cancellationToken: cancellationToken);

    private static Uri StarUri(GitHubAccount account, string repository) =>
        new(account.Host.ApiUrl, $"user/starred/{string.Join('/', repository.Split('/').Select(Uri.EscapeDataString))}");

    private static GitHubApiException StarStateError(HttpResponseMessage response) =>
        new(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub didn't accept your token. Sign out and back in to fix it.",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub said no. Check your token's Starring permission or rate limit.",
            _ => "Couldn't verify your star on GitHub. Refresh before trying again.",
        });
}
