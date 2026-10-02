// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

internal static class CodespaceFormatting
{
    public static string Subtitle(GitHubCodespace codespace, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(codespace.Branch))
        {
            parts.Add(codespace.Branch);
        }

        if (codespace.LastUsedAt != DateTimeOffset.MinValue)
        {
            parts.Add(NotificationFormatting.RelativeTime(codespace.LastUsedAt, now));
        }

        return string.Join(" \u00B7 ", parts);
    }

    public static string StateLabel(string state) => state switch
    {
        "Available" => "Active",
        "Shutdown" => "Stopped",
        "ShuttingDown" => "Stopping",
        _ => state,
    };

    public static Tag StateTag(string state)
    {
        (byte R, byte G, byte B) color = state switch
        {
            "Available" => (0x2D, 0xA4, 0x4E),
            "Created" or "Queued" or "Provisioning" or "Awaiting" or "Starting" or "ShuttingDown" or "Exporting" or "Updating" or "Rebuilding"
                => (0xBF, 0x87, 0x00),
            "Failed" or "Unavailable" => (0xE5, 0x53, 0x4B),
            _ => (0x8C, 0x95, 0x9F),
        };

        return new Tag(StateLabel(state))
        {
            Foreground = ColorHelpers.FromRgb(color.R, color.G, color.B),
            Background = ColorHelpers.FromArgb(0x33, color.R, color.G, color.B),
        };
    }
}
