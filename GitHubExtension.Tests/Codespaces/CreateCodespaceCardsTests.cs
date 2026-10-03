// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public sealed class CreateCodespaceCardsTests
{
    [TestMethod]
    [DataRow("owner/repo", "feature/branch", "Try again")]
    [DataRow("owner/\"repo\\\n\t\u263a", "feature/\"branch\\\r\n\u263a", "Error \"\\\n\t\u263a")]
    public void Form_PreservesInputAndErrorText(string repository, string branch, string error)
    {
        using var json = JsonDocument.Parse(CreateCodespaceCards.Form(repository, branch, error));
        var root = json.RootElement;
        Assert.AreEqual("AdaptiveCard", root.GetProperty("type").GetString());
        var body = root.GetProperty("body");
        Assert.AreEqual(repository, body[2].GetProperty("value").GetString());
        Assert.AreEqual(branch, body[3].GetProperty("value").GetString());
        Assert.AreEqual(error, body[4].GetProperty("text").GetString());
        Assert.AreEqual("Attention", body[4].GetProperty("color").GetString());
        Assert.AreEqual(CreateCodespaceActions.Create, body[5].GetProperty("actions")[0].GetProperty("data").GetProperty("action").GetString());
    }

    [TestMethod]
    public void Form_NullInputsAreEmptyAndNoErrorElementIsAdded()
    {
        using var json = JsonDocument.Parse(CreateCodespaceCards.Form(null, null, null));
        var body = json.RootElement.GetProperty("body");
        Assert.AreEqual(5, body.GetArrayLength());
        Assert.AreEqual("", body[2].GetProperty("value").GetString());
        Assert.AreEqual("", body[3].GetProperty("value").GetString());
        Assert.AreEqual("ActionSet", body[4].GetProperty("type").GetString());
    }

    [TestMethod]
    public void ProgressAndSuccess_PreserveRepositoryAndActions()
    {
        const string repository = "owner/\"repo\\\n\u263a";
        using var creating = JsonDocument.Parse(CreateCodespaceCards.Creating(repository));
        Assert.AreEqual(repository, creating.RootElement.GetProperty("body")[1].GetProperty("text").GetString());
        var space = new GitHubCodespace("test", null, repository, null, "Queued", DateTimeOffset.MinValue, new Uri("https://test.github.dev"));

        using var created = JsonDocument.Parse(CreateCodespaceCards.Created(space));
        var body = created.RootElement.GetProperty("body");
        Assert.AreEqual($"GitHub is preparing your development environment for {repository}.", body[1].GetProperty("text").GetString());
        var actions = body[2].GetProperty("actions");
        Assert.AreEqual(2, actions.GetArrayLength());
        Assert.AreEqual(CreateCodespaceActions.Open, actions[0].GetProperty("data").GetProperty("action").GetString());
        Assert.AreEqual(CreateCodespaceActions.CreateAnother, actions[1].GetProperty("data").GetProperty("action").GetString());
        Assert.AreEqual("none", actions[1].GetProperty("associatedInputs").GetString());
    }
}
