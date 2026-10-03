// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public class RepositoryParsingTests
{
    private const string PowerToys = """
        {
          "full_name": "microsoft/PowerToys",
          "html_url": "https://github.com/microsoft/PowerToys",
          "description": "Windows system utilities",
          "private": false,
          "fork": false,
          "archived": false,
          "language": "C#",
          "stargazers_count": 112400,
          "forks_count": 6700,
          "pushed_at": "2025-06-01T11:48:00Z",
          "clone_url": "https://github.com/microsoft/PowerToys.git"
        }
        """;

    [TestMethod]
    public void ParseRepository_ReadsEveryField()
    {
        using var doc = JsonDocument.Parse(PowerToys);

        var repo = RepositoriesClient.ParseRepository(doc.RootElement)!;

        Assert.AreEqual("microsoft/PowerToys", repo.FullName);
        Assert.AreEqual(new Uri("https://github.com/microsoft/PowerToys"), repo.WebUrl);
        Assert.AreEqual("Windows system utilities", repo.Description);
        Assert.IsFalse(repo.Private);
        Assert.AreEqual("C#", repo.Language);
        Assert.AreEqual(112400, repo.Stars);
        Assert.AreEqual(6700, repo.Forks);
        Assert.AreEqual(new DateTimeOffset(2025, 6, 1, 11, 48, 0, TimeSpan.Zero), repo.PushedAt);
        Assert.AreEqual(new Uri("https://github.com/microsoft/PowerToys.git"), repo.CloneUrl);
    }

    [TestMethod]
    [DataRow("""{ "html_url": "https://github.com/o/r" }""")]
    [DataRow("""{ "full_name": "o/r" }""")]
    [DataRow("""{ "full_name": " ", "html_url": "https://github.com/o/r" }""")]
    [DataRow("""{ "full_name": 42, "html_url": "https://github.com/o/r" }""")]
    [DataRow("""{ "full_name": "o/r", "html_url": "not a URL" }""")]
    [DataRow("""{ "full_name": "o/r", "html_url": "file:///tmp/repo" }""")]
    [DataRow("""{ "full_name": "o/r", "html_url": "https://user@github.com/o/r" }""")]
    [DataRow("null")]
    [DataRow("42")]
    public void ParseRepository_RejectsIncompleteEntries(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.ThrowsExactly<GitHubApiException>(() => RepositoriesClient.ParseRepository(doc.RootElement));
    }

    [TestMethod]
    public void ParseRepositories_RejectsPartialResults()
    {
        using var doc = JsonDocument.Parse($"[{PowerToys}, {{ \"full_name\": \"broken\" }}]");

        Assert.ThrowsExactly<GitHubApiException>(() => RepositoriesClient.ParseRepositories(doc.RootElement));
    }

    [TestMethod]
    public void ParseRepository_HandlesNullLanguageAndDescription()
    {
        using var doc = JsonDocument.Parse("""
            { "full_name": "o/r", "html_url": "https://github.com/o/r", "description": null, "language": null, "private": true }
            """);

        var repo = RepositoriesClient.ParseRepository(doc.RootElement)!;

        Assert.IsNull(repo.Description);
        Assert.IsNull(repo.Language);
        Assert.IsTrue(repo.Private);
        Assert.AreEqual(DateTimeOffset.MinValue, repo.PushedAt);
    }

    [TestMethod]
    public void ParseRepository_AllowsAbsentOptionalFields()
    {
        using var doc = JsonDocument.Parse("""{"full_name":"o/r","html_url":"https://github.com/o/r"}""");

        var repo = RepositoriesClient.ParseRepository(doc.RootElement);

        Assert.IsNull(repo.Description);
        Assert.IsNull(repo.Language);
        Assert.IsNull(repo.CloneUrl);
        Assert.AreEqual(0, repo.Stars);
        Assert.IsFalse(repo.Private);
    }
}
