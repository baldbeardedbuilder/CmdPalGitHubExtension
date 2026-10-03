// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

internal sealed record UpdateBranchRequest([property: JsonPropertyName("expected_head_sha")] string ExpectedHeadSha);
internal sealed record CreatePendingReviewRequest(
    [property: JsonPropertyName("commit_id")] string CommitId,
    [property: JsonPropertyName("body")] string Body);
internal sealed record SubmitReviewRequest(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("body")] string Body);

[JsonSerializable(typeof(UpdateBranchRequest))]
[JsonSerializable(typeof(CreatePendingReviewRequest))]
[JsonSerializable(typeof(SubmitReviewRequest))]
internal sealed partial class PullRequestFeatureJsonContext : JsonSerializerContext;
