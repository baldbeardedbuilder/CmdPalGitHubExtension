// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal interface IWorkflowCancellationPermissionsClient
{
    Task<bool> CanCancelAsync(GitHubAccount account, string repository, CancellationToken cancellationToken);
}

internal sealed partial class ActionsClient
{
    public Task<bool> CanCancelAsync(GitHubAccount account, string repository, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
            using var response = await SendAsync(httpClient, account, HttpMethod.Get,
                new Uri(account.Host.ApiUrl, $"repos/{path}"), cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || !json.RootElement.TryGetProperty("permissions", out var permissions)
                    || permissions.ValueKind != JsonValueKind.Object)
                {
                    throw new GitHubApiException("Couldn't verify permission to cancel this workflow. Check your repository access and token's Actions write permission.");
                }

                return GetBool(permissions, "push") || GetBool(permissions, "maintain") || GetBool(permissions, "admin");
            });
        }, cancellationToken: cancellationToken);
}
