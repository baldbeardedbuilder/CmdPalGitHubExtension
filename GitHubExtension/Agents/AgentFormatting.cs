// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Agents;

internal static class AgentFormatting
{
    public static string StateText(string state) => state switch
    {
        "in_progress" => "Working",
        "queued" or "idle" or "waiting_for_user" => "Waiting",
        "completed" => "Done",
        "failed" => "Failed",
        "timed_out" => "Timed out",
        "cancelled" => "Cancelled",
        _ => state,
    };

    public static Tag StateTag(string state)
    {
        (byte R, byte G, byte B) color = state switch
        {
            "in_progress" => (0x69, 0x73, 0xFF),
            "completed" => (0x2D, 0xA4, 0x4E),
            "failed" or "timed_out" => (0xE5, 0x53, 0x4B),
            _ => (0x8C, 0x95, 0x9F),
        };
        return new Tag(StateText(state))
        {
            Foreground = ColorHelpers.FromRgb(color.R, color.G, color.B),
            Background = ColorHelpers.FromArgb(0x33, color.R, color.G, color.B),
        };
    }

    public static string Subtitle(GitHubAgentTask task, DateTimeOffset now)
    {
        var parts = new List<string>
        {
            task.RepositoryFullName ?? (task.RepositoryId is null ? "No repository" : "Repository unavailable"),
        };
        if (!string.IsNullOrWhiteSpace(task.Model))
        {
            parts.Add(task.Model);
        }

        parts.Add(NotificationFormatting.RelativeTime(task.UpdatedAt, now));
        if (task.DetailsError is { } error)
        {
            parts.Add(error);
        }

        return string.Join(" \u00b7 ", parts);
    }
}
