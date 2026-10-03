// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json.Serialization;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal sealed record WorkflowDispatchRequest(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("inputs")] Dictionary<string, string> Inputs);

[JsonSerializable(typeof(WorkflowDispatchRequest))]
internal sealed partial class ActionsJsonContext : JsonSerializerContext;
