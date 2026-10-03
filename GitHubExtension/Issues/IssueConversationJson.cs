// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal sealed record IssueCommentRequest([property: JsonPropertyName("body")] string Body);

[JsonSerializable(typeof(IssueCommentRequest))]
internal sealed partial class IssueConversationJsonContext : JsonSerializerContext;
