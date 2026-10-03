// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Xml.Linq;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests;

[TestClass]
public class BuildConfigurationTests
{
    [TestMethod]
    public void Project_DoesNotTerminateRunningProcesses()
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GitHubExtension.csproj"));

        Assert.IsFalse(project.Descendants("Target")
            .Any(target => (string?)target.Attribute("Name") == "KillRunningExecutable"));

        foreach (var exec in project.Descendants("Exec"))
        {
            var command = (string?)exec.Attribute("Command") ?? string.Empty;
            Assert.IsFalse(command.Contains("taskkill", StringComparison.OrdinalIgnoreCase),
                "Build commands must not terminate extension processes.");
            Assert.IsFalse(command.Contains("Stop-Process", StringComparison.OrdinalIgnoreCase),
                "Build commands must not terminate extension processes.");
        }
    }
}
