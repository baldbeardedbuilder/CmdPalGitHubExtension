// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Notifications;

internal static class NotificationFormatting
{
    private static readonly (byte R, byte G, byte B) Green = (0x2D, 0xA4, 0x4E);
    private static readonly (byte R, byte G, byte B) Purple = (0x98, 0x6E, 0xE2);
    private static readonly (byte R, byte G, byte B) Red = (0xE5, 0x53, 0x4B);
    private static readonly (byte R, byte G, byte B) Gray = (0x8C, 0x95, 0x9F);

    /// <summary>
    /// Only issues and pull requests have a state worth a badge, so those are the only subjects we look up.
    /// </summary>
    public static bool HasState(GitHubNotification notification) =>
        notification.SubjectApiUrl is not null && notification.SubjectType is "Issue" or "PullRequest";

    public static string Glyph(string subjectType) => subjectType switch
    {
        "PullRequest" => "git-pull-request",
        "Issue" => "issue-opened",
        "Discussion" => "comment-discussion",
        "CheckSuite" or "WorkflowRun" => "zap",
        "Release" => "tag",
        "Commit" => "git-commit",
        "RepositoryVulnerabilityAlert" or "RepositoryDependabotAlertsThread" or "SecurityAdvisory" => "shield",
        _ => "bell",
    };

    public static Tag? StateTag(string subjectType, SubjectState state)
    {
        var isPullRequest = subjectType == "PullRequest";
        return state switch
        {
            SubjectState.Open => Badge("Open", isPullRequest ? Icons.StateOpenPullRequest : Icons.StateOpenIssue, Green),
            SubjectState.Draft => Badge("Draft", Icons.StateDraft, Gray),
            SubjectState.Merged => Badge("Merged", Icons.StateMerged, Purple),
            SubjectState.Closed => isPullRequest
                ? Badge("Closed", Icons.StateClosedPullRequest, Red)
                : Badge("Closed", Icons.StateClosedIssue, Purple),
            SubjectState.NotPlanned => Badge("Not planned", Icons.StateNotPlanned, Gray),
            _ => null,
        };
    }

    /// <summary>
    /// Formats a timestamp the way GitHub does in lists: "just now", "45m ago", "2h ago", "3d ago", then a date.
    /// </summary>
    public static string RelativeTime(DateTimeOffset when, DateTimeOffset now)
    {
        var elapsed = now - when;
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{(int)elapsed.TotalMinutes}m ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"{(int)elapsed.TotalHours}h ago";
        }

        if (elapsed < TimeSpan.FromDays(30))
        {
            return $"{(int)elapsed.TotalDays}d ago";
        }

        var local = when.ToLocalTime();
        var format = local.Year == now.ToLocalTime().Year ? "MMM d" : "MMM d, yyyy";
        return local.ToString(format, CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Builds a browser url for a notification when we don't have one from the API.
    /// </summary>
    public static Uri WebUrl(GitHubHost host, GitHubNotification notification)
    {
        var repo = notification.RepositoryWebUrl ?? new Uri(host.WebUrl, notification.RepositoryFullName);
        var repoBase = repo.AbsoluteUri.TrimEnd('/') + "/";

        if (notification.SubjectApiUrl is { } api
            && api.AbsoluteUri.StartsWith(host.ApiUrl.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            // repos/{owner}/{repo}/{kind}/{id}
            var segments = api.AbsoluteUri[host.ApiUrl.AbsoluteUri.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 5 && segments[0] == "repos")
            {
                var kind = segments[3] switch
                {
                    "pulls" => "pull",
                    "issues" => "issues",
                    "commits" => "commit",
                    _ => null,
                };

                if (kind is not null)
                {
                    return new Uri($"{repoBase}{kind}/{segments[4]}");
                }
            }
        }

        return notification.SubjectType switch
        {
            "Discussion" => new Uri($"{repoBase}discussions"),
            "CheckSuite" or "WorkflowRun" => new Uri($"{repoBase}actions"),
            "Release" => new Uri($"{repoBase}releases"),
            "RepositoryVulnerabilityAlert" or "RepositoryDependabotAlertsThread" => new Uri($"{repoBase}security/dependabot"),
            _ => repo,
        };
    }

    private static Tag Badge(string text, IconInfo icon, (byte R, byte G, byte B) color) => new(text)
    {
        Icon = icon,
        Foreground = ColorHelpers.FromRgb(color.R, color.G, color.B),
        Background = ColorHelpers.FromArgb(0x33, color.R, color.G, color.B),
    };
}
