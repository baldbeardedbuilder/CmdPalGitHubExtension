using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal sealed record RepositorySubscription(bool Subscribed, bool Ignored);
internal enum RepositoryWatchAction { Watch, Unwatch, Ignore }

internal interface IRepositoryWatchingClient
{
    Task<RepositorySubscription> GetWatchingAsync(GitHubAccount account, string repository, CancellationToken token);
    Task SetWatchingAsync(GitHubAccount account, string repository, RepositoryWatchAction action, CancellationToken token);
}

internal sealed partial class RepositoriesClient : IRepositoryWatchingClient
{
    public Task<RepositorySubscription> GetWatchingAsync(GitHubAccount account, string repository, CancellationToken token) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Repositories, async () =>
        {
            var uri = WatchingUri(account, repository);
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, token, throwOnError: false).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                using var accessible = await SendAsync(httpClient, account, HttpMethod.Get,
                    new Uri(uri.AbsoluteUri[..^"/subscription".Length]), token).ConfigureAwait(false);
                return new RepositorySubscription(false, false);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw SsoRequired(account, response) ?? new GitHubApiException(
                    "Couldn't read your watching state. Check repository access, token permissions, and your rate limit.");
            }

            using var json = await ReadJsonAsync(response, token).ConfigureAwait(false);
            var root = json.RootElement;
            if (!root.TryGetProperty("subscribed", out var subscribed)
                || subscribed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("ignored", out var ignored)
                || ignored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new GitHubApiException("GitHub sent back a watching state we couldn't read.");
            }

            return new RepositorySubscription(subscribed.GetBoolean(), ignored.GetBoolean());
        }, cancellationToken: token);

    public async Task SetWatchingAsync(GitHubAccount account, string repository, RepositoryWatchAction action, CancellationToken token)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        using var content = action == RepositoryWatchAction.Unwatch ? null : new StringContent(
            action == RepositoryWatchAction.Ignore ? """{"subscribed":false,"ignored":true}""" : """{"subscribed":true,"ignored":false}""",
            Encoding.UTF8, "application/json");
        using var response = await SendMutationAsync(httpClient, account,
            action == RepositoryWatchAction.Unwatch ? HttpMethod.Delete : HttpMethod.Put,
            WatchingUri(account, repository), token, content: content).ConfigureAwait(false);
    }

    internal static Uri WatchingUri(GitHubAccount account, string repository)
    {
        var parts = repository.Split('/');
        if (parts.Length != 2 || parts.Any(p => string.IsNullOrWhiteSpace(p) || p.Contains('\\') || p is "." or ".."))
        {
            throw new GitHubApiException("Enter a repository as owner/name.");
        }

        return new Uri(account.Host.ApiUrl, $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/subscription");
    }
}
