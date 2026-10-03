// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Xml.Linq;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests;

[TestClass]
public class BuildConfigurationTests
{
    [TestMethod]
    [DataRow("GitHubOAuthClientId", "GH_OAUTH_CLIENT_ID")]
    [DataRow("GitHubOAuthClientSecret", "GH_OAUTH_CLIENT_SECRET")]
    public void Project_OAuthInputs_UseGhEnvironmentVariables(string propertyName, string variableName)
    {
        var project = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "GitHubExtension.csproj"));
        var property = Assert.ContainsSingle(project.Descendants(propertyName));

        Assert.AreEqual($"$({variableName})", property.Value);
        Assert.AreEqual($"'$({propertyName})' == ''", (string?)property.Attribute("Condition"));

        var metadata = Assert.ContainsSingle(project.Descendants("AssemblyMetadata")
            .Where(item => (string?)item.Attribute("Include") == propertyName));
        Assert.AreEqual($"$({propertyName})", (string?)metadata.Attribute("Value"));

        Assert.IsFalse(project.Descendants().Attributes()
            .Any(attribute => attribute.Value.Contains("$(GITHUB_OAUTH_", StringComparison.Ordinal)));
        Assert.IsFalse(project.Descendants().Where(element => !element.HasElements)
            .Any(element => element.Value.Contains("$(GITHUB_OAUTH_", StringComparison.Ordinal)));
    }

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
