// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public class CodespaceFormattingTests
{
    [TestMethod]
    [DataRow("Available", "Active", 0x2D, 0xA4, 0x4E)]
    [DataRow("Shutdown", "Stopped", 0x8C, 0x95, 0x9F)]
    [DataRow("Rebuilding", "Rebuilding", 0xBF, 0x87, 0x00)]
    [DataRow("Starting", "Starting", 0xBF, 0x87, 0x00)]
    [DataRow("ShuttingDown", "Stopping", 0xBF, 0x87, 0x00)]
    [DataRow("Failed", "Failed", 0xE5, 0x53, 0x4B)]
    [DataRow("Unavailable", "Unavailable", 0xE5, 0x53, 0x4B)]
    [DataRow("NewState", "NewState", 0x8C, 0x95, 0x9F)]
    public void StateTag_LabelsAndColorsReflectStatus(string state, string label, int r, int g, int b)
    {
        var tag = CodespaceFormatting.StateTag(state);

        Assert.AreEqual(label, tag.Text);
        Assert.AreEqual((byte)r, tag.Foreground.Color.R);
        Assert.AreEqual((byte)g, tag.Foreground.Color.G);
        Assert.AreEqual((byte)b, tag.Foreground.Color.B);
        Assert.AreEqual((byte)0x33, tag.Background.Color.A);
    }

    [TestMethod]
    public void Subtitle_MissingBranchAndTimestampDoesNotInventValues()
    {
        var now = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var codespace = new GitHubCodespace("one", null, "o/r", null, "Unknown", DateTimeOffset.MinValue, new Uri("https://one.github.dev"));

        Assert.AreEqual(string.Empty, CodespaceFormatting.Subtitle(codespace, now));
        Assert.AreEqual("main", CodespaceFormatting.Subtitle(codespace with { Branch = "main" }, now));
        Assert.AreEqual("1d ago", CodespaceFormatting.Subtitle(codespace with { LastUsedAt = now.AddDays(-1) }, now));
    }
}
