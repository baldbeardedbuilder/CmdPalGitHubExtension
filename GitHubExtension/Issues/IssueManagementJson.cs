// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace BaldBeardedBuilder.CmdPal.GitHub.Issues;

internal sealed record IssueWriteRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("milestone")] int? Milestone);

[JsonSerializable(typeof(IssueWriteRequest))]
internal sealed partial class IssueManagementJsonContext : JsonSerializerContext;
