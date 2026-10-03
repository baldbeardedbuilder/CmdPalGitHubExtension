// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal sealed record ThreadSubscription(bool Subscribed, bool Ignored, bool HasSubscription = true)
{
    internal static readonly ThreadSubscription Default = new(false, false, false);
}

internal enum ThreadSubscriptionAction { Subscribe, Unsubscribe, Ignore }

internal interface IThreadSubscriptionsClient
{
    Task<ThreadSubscription> GetSubscriptionAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken);
    Task SetSubscriptionAsync(GitHubAccount account, string threadId, ThreadSubscriptionAction action, CancellationToken cancellationToken);
}

internal sealed partial class NotificationsClient
{
    public Task<ThreadSubscription> GetSubscriptionAsync(GitHubAccount account, string threadId, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Notifications, async () =>
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, SubscriptionUri(account, threadId),
                cancellationToken, throwOnError: false).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                using var thread = await SendAsync(httpClient, account, HttpMethod.Get,
                    new Uri(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}"),
                    cancellationToken).ConfigureAwait(false);
                using var threadJson = await ReadJsonAsync(thread, cancellationToken).ConfigureAwait(false);
                if (threadJson.RootElement.ValueKind != JsonValueKind.Object || GetString(threadJson.RootElement, "id") != threadId)
                {
                    throw new GitHubApiException("Couldn't verify this notification thread. Refresh before changing its subscription.");
                }

                return ThreadSubscription.Default;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw SsoRequired(account, response) ?? new GitHubApiException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "GitHub didn't accept your token. Sign out and back in to fix it.",
                    HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub said no. Use a classic token with notifications or repo scope, and check your rate limit.",
                    _ => "Couldn't load this thread's subscription. Refresh before trying again.",
                });
            }

            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return ParseSubscription(json.RootElement);
        }, cancellationToken: cancellationToken);

    public async Task SetSubscriptionAsync(GitHubAccount account, string threadId, ThreadSubscriptionAction action, CancellationToken cancellationToken)
    {
        var sent = false;
        await DomainDiagnostics.RunAsync(DiagnosticArea.Notifications, async () =>
        {
            using var content = action == ThreadSubscriptionAction.Unsubscribe ? null
                : new StringContent(action == ThreadSubscriptionAction.Ignore
                    ? """{"ignored":true}""" : """{"ignored":false}""", Encoding.UTF8, "application/json");
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            using var response = await SendMutationAsync(httpClient, account,
                action == ThreadSubscriptionAction.Unsubscribe ? HttpMethod.Delete : HttpMethod.Put,
                SubscriptionUri(account, threadId), cancellationToken, content: content).ConfigureAwait(false);
            return response.IsAccepted ? DiagnosticOutcome.Accepted : DiagnosticOutcome.Completed;
        }, DiagnosticEvent.Mutation, outcome => outcome, () => sent, cancellationToken).ConfigureAwait(false);
    }

    internal static ThreadSubscription ParseSubscription(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Notifications, () =>
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("subscribed", out var subscribed)
                || subscribed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("ignored", out var ignored)
                || ignored.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new GitHubApiException("GitHub sent back a thread subscription we couldn't read. Try refreshing.");
            }

            return new ThreadSubscription(subscribed.GetBoolean(), ignored.GetBoolean());
        });

    private static Uri SubscriptionUri(GitHubAccount account, string threadId) =>
        new(account.Host.ApiUrl, $"notifications/threads/{Uri.EscapeDataString(threadId)}/subscription");
}
