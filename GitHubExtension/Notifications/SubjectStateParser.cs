// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal static class SubjectStateParser
{
    internal static SubjectState Parse(JsonElement element)
    {
        var merged = GetBool(element, "merged") || GetString(element, "merged_at") is not null;
        var draft = GetBool(element, "draft");

        return GetString(element, "state") switch
        {
            _ when merged => SubjectState.Merged,
            "open" when draft => SubjectState.Draft,
            "open" => SubjectState.Open,
            "closed" when GetString(element, "state_reason") == "not_planned" => SubjectState.NotPlanned,
            "closed" => SubjectState.Closed,
            _ => SubjectState.Unknown,
        };
    }
}
