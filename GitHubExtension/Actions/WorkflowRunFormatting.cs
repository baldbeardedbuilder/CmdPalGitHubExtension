// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal static class WorkflowRunFormatting
{
    public static string Subtitle(GitHubWorkflowRun run, DateTimeOffset now)
    {
        var parts = new List<string> { run.DisplayTitle };
        if (run.Actor.Length > 0)
        {
            parts.Add(run.Actor);
        }

        if (run.CreatedAt != DateTimeOffset.MinValue)
        {
            parts.Add(NotificationFormatting.RelativeTime(run.CreatedAt, now));
        }

        return string.Join(" \u00B7 ", parts);
    }

    public static string State(GitHubWorkflowRun run) => run.Status switch
    {
        "in_progress" => "In progress",
        "queued" or "requested" or "waiting" or "pending" => "Queued",
        "completed" => run.Conclusion switch
        {
            "success" => "Success",
            "failure" => "Failure",
            "timed_out" => "Timed out",
            "cancelled" => "Cancelled",
            "skipped" => "Skipped",
            "neutral" => "Neutral",
            "action_required" => "Action required",
            "stale" => "Stale",
            _ => "Unknown",
        },
        _ => "Unknown",
    };

    public static IconInfo Icon(GitHubWorkflowRun run) => State(run) switch
    {
        "Success" => Icons.RunSuccess,
        "Failure" or "Timed out" => Icons.RunFailure,
        "In progress" => Icons.RunInProgress,
        _ => Icons.RunNeutral,
    };
}
