// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public class RepoFormattingTests
{
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] PrivateAndLanguage = ["Private", "C#"];

    [TestMethod]
    [DataRow(0, "0")]
    [DataRow(841, "841")]
    [DataRow(1000, "1k")]
    [DataRow(6700, "6.7k")]
    [DataRow(6799, "6.7k")]
    [DataRow(112400, "112.4k")]
    [DataRow(1200000, "1.2m")]
    public void CompactCount_ShortensLikeGitHub(int count, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.AreEqual(expected, RepoFormatting.CompactCount(count));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [TestMethod]
    public void Subtitle_ShowsStarsForksAndLastPush()
    {
        var subtitle = RepoFormatting.Subtitle(Repo(pushedAt: Now.AddMinutes(-12)), Now);

        Assert.AreEqual("\u2606 841 \u00B7 \u2442 12 \u00B7 \U0001F551 12m ago", subtitle);
    }

    [TestMethod]
    public void Subtitle_SkipsTimeWhenNeverPushed()
    {
        Assert.AreEqual("\u2606 841 \u00B7 \u2442 12", RepoFormatting.Subtitle(Repo(pushedAt: DateTimeOffset.MinValue), Now));
    }

    [TestMethod]
    public void Tags_ShowPrivateThenLanguage()
    {
        var tags = RepoFormatting.Tags(Repo(isPrivate: true, language: "C#"));

        CollectionAssert.AreEqual(PrivateAndLanguage, tags.Select(t => t.Text).ToArray());
    }

    [TestMethod]
    public void Tags_EmptyForPublicRepoWithoutLanguage()
    {
        Assert.IsEmpty(RepoFormatting.Tags(Repo(language: null)));
    }

    [TestMethod]
    public void LanguageColor_UsesLinguistColor()
    {
        Assert.AreEqual(((byte)0x31, (byte)0x78, (byte)0xC6), RepoFormatting.LanguageColor("typescript"));
    }

    [TestMethod]
    public void LanguageColor_LiftsDarkColors()
    {
        var (r, g, b) = RepoFormatting.LanguageColor("PowerShell");

        Assert.IsGreaterThan(0x01, r);
        Assert.IsGreaterThan(0x24, g);
        Assert.IsGreaterThan(0x56, b);
    }

    [TestMethod]
    public void LanguageColor_FallsBackToGray()
    {
        Assert.AreEqual(((byte)0x8C, (byte)0x95, (byte)0x9F), RepoFormatting.LanguageColor("Brainfork"));
    }

    internal static GitHubRepository Repo(
        string fullName = "o/r",
        bool isPrivate = false,
        string? language = "C#",
        string? description = null,
        DateTimeOffset? pushedAt = null) => new(
            fullName,
            new Uri($"https://github.com/{fullName}"),
            description,
            isPrivate,
            false,
            false,
            language,
            841,
            12,
            pushedAt ?? new DateTimeOffset(2025, 6, 1, 11, 48, 0, TimeSpan.Zero),
            new Uri($"https://github.com/{fullName}.git"));
}
