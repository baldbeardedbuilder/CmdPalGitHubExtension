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
