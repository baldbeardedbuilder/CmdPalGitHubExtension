// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Windows.UI.ViewManagement;

namespace BaldBeardedBuilder.CmdPal.GitHub;

internal static class Icons
{
    private const string LightMarkPath = "Assets\\GitHubMark.light.png";
    private const string DarkMarkPath = "Assets\\GitHubMark.dark.png";

    internal static IconInfo GitHub { get; } = new(
        IconHelpers.FromRelativePath(LightMarkPath).Light,
        IconHelpers.FromRelativePath(DarkMarkPath).Dark);

    internal static IconInfo SignOut { get; } = new("\uF3B1");

    internal static IconInfo Account { get; } = new("\uE77B");

    internal static IconInfo Refresh { get; } = new("\uE72C");

    internal static IconInfo MarkRead { get; } = new("\uE8C3");

    internal static IconInfo Done { get; } = new("\uE73E");

    internal static IconInfo Copy { get; } = new("\uE8C8");

    internal static IconInfo Notifications { get; } = Themed("bell");

    internal static IconInfo SavedQueries { get; } = Themed("search");

    internal static IconInfo Repos { get; } = Themed("repo");

    internal static IconInfo Actions { get; } = new("\uE945");

    internal static IconInfo RunSuccess { get; } = Themed("run-success");

    internal static IconInfo RunFailure { get; } = Themed("run-failure");

    internal static IconInfo RunInProgress { get; } = Themed("run-in-progress");

    internal static IconInfo RunNeutral { get; } = Themed("run-neutral");

    internal static IconInfo Agents { get; } = Themed("copilot");

    internal static IconInfo Codespaces { get; } = Themed("codespaces");

    internal static IconInfo Issues { get; } = Themed("issue-opened");

    internal static IconInfo Discussions { get; } = new("\uE8F2");

    internal static IconInfo PullRequests { get; } = Themed("git-pull-request");

    internal static IconInfo StateOpenIssue { get; } = Octicon("state-issue-opened.svg");

    internal static IconInfo StateOpenPullRequest { get; } = Octicon("state-git-pull-request.svg");

    internal static IconInfo StateDraft { get; } = Octicon("state-git-pull-request-draft.svg");

    internal static IconInfo StateMerged { get; } = Octicon("state-git-merge.svg");

    internal static IconInfo StateClosedIssue { get; } = Octicon("state-issue-closed.svg");

    internal static IconInfo StateNotPlanned { get; } = Octicon("state-skip.svg");

    internal static IconInfo StateClosedPullRequest { get; } = Octicon("state-git-pull-request-closed.svg");

    private static readonly Dictionary<(string Glyph, bool Unread), IconInfo> ListIcons = [];

    /// <summary>
    /// Notification row icons share a layout with a leading unread dot so read and unread rows line up.
    /// </summary>
    internal static IconInfo NotificationIcon(string glyph, bool unread)
    {
        lock (ListIcons)
        {
            if (!ListIcons.TryGetValue((glyph, unread), out var icon))
            {
                var suffix = unread ? ".unread" : string.Empty;
                icon = Themed($"list-{glyph}{suffix}");
                ListIcons[(glyph, unread)] = icon;
            }

            return icon;
        }
    }

    private static IconInfo Themed(string name) => new(
        IconHelpers.FromRelativePath($"Assets\\Octicons\\{name}.light.svg").Light,
        IconHelpers.FromRelativePath($"Assets\\Octicons\\{name}.dark.svg").Dark);

    private static IconInfo Octicon(string file) => IconHelpers.FromRelativePath($"Assets\\Octicons\\{file}");

    private static readonly Lazy<string> LightMarkDataUri = new(() => ToDataUri(LightMarkPath));
    private static readonly Lazy<string> DarkMarkDataUri = new(() => ToDataUri(DarkMarkPath));

    /// <summary>
    /// Adaptive Cards can't swap images by theme, so pick the mark that contrasts with the current Windows theme.
    /// </summary>
    internal static string GetGitHubMarkDataUri() => IsDarkTheme() ? DarkMarkDataUri.Value : LightMarkDataUri.Value;

    private static bool IsDarkTheme()
    {
        try
        {
            var foreground = new UISettings().GetColorValue(UIColorType.Foreground);
            return foreground.R > 128;
        }
        catch (COMException)
        {
            return true;
        }
    }

    private static string ToDataUri(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        return File.Exists(path)
            ? "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path))
            : string.Empty;
    }
}
