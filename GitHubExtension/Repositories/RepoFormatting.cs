// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Repositories;

internal static class RepoFormatting
{
    private static readonly (byte R, byte G, byte B) Gray = (0x8C, 0x95, 0x9F);

    // Colors from github-linguist so the badges match what you see on github.com.
    private static readonly Dictionary<string, string> LanguageColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Astro"] = "#ff5a03",
        ["Bicep"] = "#519aba",
        ["C"] = "#555555",
        ["C#"] = "#178600",
        ["C++"] = "#f34b7d",
        ["CSS"] = "#563d7c",
        ["Dart"] = "#00B4AB",
        ["Dockerfile"] = "#384d54",
        ["Elixir"] = "#6e4a7e",
        ["F#"] = "#b845fc",
        ["Go"] = "#00ADD8",
        ["HCL"] = "#844FBA",
        ["HTML"] = "#e34c26",
        ["Haskell"] = "#5e5086",
        ["Java"] = "#b07219",
        ["JavaScript"] = "#f1e05a",
        ["Jupyter Notebook"] = "#DA5B0B",
        ["Kotlin"] = "#A97BFF",
        ["Lua"] = "#000080",
        ["MDX"] = "#fcb32c",
        ["Nix"] = "#7e7eff",
        ["Objective-C"] = "#438eff",
        ["PHP"] = "#4F5D95",
        ["PowerShell"] = "#012456",
        ["Python"] = "#3572A5",
        ["Ruby"] = "#701516",
        ["Rust"] = "#dea584",
        ["SCSS"] = "#c6538c",
        ["Scala"] = "#c22d40",
        ["Shell"] = "#89e051",
        ["Svelte"] = "#ff3e00",
        ["Swift"] = "#F05138",
        ["TypeScript"] = "#3178c6",
        ["Visual Basic .NET"] = "#945db7",
        ["Vue"] = "#41b883",
        ["Zig"] = "#ec915c",
    };

    public static string Subtitle(GitHubRepository repository, DateTimeOffset now)
    {
        var parts = new List<string>
        {
            $"\u2606 {CompactCount(repository.Stars)}",
            $"\u2442 {CompactCount(repository.Forks)}",
        };

        if (repository.PushedAt != DateTimeOffset.MinValue)
        {
            parts.Add($"\U0001F551 {NotificationFormatting.RelativeTime(repository.PushedAt, now)}");
        }

        return string.Join(" \u00B7 ", parts);
    }

    /// <summary>
    /// Shortens counts the way GitHub does: 841, 6.7k, 112.4k, 1.2m.
    /// </summary>
    public static string CompactCount(int count) => count switch
    {
        < 1_000 => count.ToString(CultureInfo.CurrentCulture),
        < 1_000_000 => Shorten(count / 1_000d) + "k",
        _ => Shorten(count / 1_000_000d) + "m",
    };

    public static Tag[] Tags(GitHubRepository repository)
    {
        var tags = new List<Tag>();
        if (repository.Private)
        {
            tags.Add(Badge("Private", Gray));
        }

        if (repository.Archived)
        {
            tags.Add(Badge("Archived", Gray));
        }

        if (!string.IsNullOrEmpty(repository.Language))
        {
            tags.Add(Badge(repository.Language, LanguageColor(repository.Language)));
        }

        return [.. tags];
    }

    /// <summary>
    /// The linguist color for a language, lifted a bit when it's too dark to read on a dark background.
    /// </summary>
    internal static (byte R, byte G, byte B) LanguageColor(string language)
    {
        if (!LanguageColors.TryGetValue(language, out var hex))
        {
            return Gray;
        }

        var r = Convert.ToByte(hex.Substring(1, 2), 16);
        var g = Convert.ToByte(hex.Substring(3, 2), 16);
        var b = Convert.ToByte(hex.Substring(5, 2), 16);

        var luminance = ((0.2126 * r) + (0.7152 * g) + (0.0722 * b)) / 255;
        if (luminance >= 0.2)
        {
            return (r, g, b);
        }

        static byte Lift(byte c) => (byte)(c + ((255 - c) * 0.45));
        return (Lift(r), Lift(g), Lift(b));
    }

    private static string Shorten(double value) =>
        (Math.Floor(value * 10) / 10).ToString("0.#", CultureInfo.CurrentCulture);

    private static Tag Badge(string text, (byte R, byte G, byte B) color) => new(text)
    {
        Foreground = ColorHelpers.FromRgb(color.R, color.G, color.B),
        Background = ColorHelpers.FromArgb(0x33, color.R, color.G, color.B),
    };
}
