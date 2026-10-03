// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using System.Text.Json;
using Microsoft.CommandPalette.Extensions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
public class MutationConfirmationTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");

    [TestMethod]
    public void Cards_PreserveEscapedValuesAndAuthorizationLink()
    {
        const string escaped = "quote\"\\line\n\t\u263a";
        var account = Account with { Login = escaped };
        using var card = JsonDocument.Parse(MutationConfirmation.Card(account, escaped, escaped, escaped, escaped));
        var body = card.RootElement.GetProperty("body");
        Assert.AreEqual(escaped, body[0].GetProperty("text").GetString());
        Assert.AreEqual($"Account: {escaped}\nHost: {account.Host.WebUrl}\nTarget: {escaped}", body[1].GetProperty("text").GetString());
        Assert.AreEqual(escaped, body[2].GetProperty("text").GetString());
        Assert.AreEqual(escaped, body[3].GetProperty("actions")[0].GetProperty("data").GetProperty("action").GetString());
        var url = new Uri("https://github.com/orgs/test/sso?return_to=%22quoted%22");
        using var result = JsonDocument.Parse(MutationConfirmation.ResultCard(escaped, url));
        var resultBody = result.RootElement.GetProperty("body");
        Assert.AreEqual(escaped, resultBody[0].GetProperty("text").GetString());
        Assert.AreEqual(url.AbsoluteUri, resultBody[1].GetProperty("actions")[0].GetProperty("url").GetString());
        using var withoutLink = JsonDocument.Parse(MutationConfirmation.ResultCard(escaped, null));
        Assert.AreEqual(1, withoutLink.RootElement.GetProperty("body").GetArrayLength());
    }

    [TestMethod]
    [DataRow("42")]
    [DataRow("null")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("{")]
    public void MalformedOrUnrecognizedAction_DoesNotSubmit(string data)
    {
        var writes = 0;
        var page = new MutationConfirmationPage(Account, "Start", "target", "May incur charges.",
            () => { writes++; return Task.CompletedTask; }, () => ("Complete", null), () => true);
        Form(page).SubmitForm("{}", data);
        Assert.AreEqual(0, writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StaleBeforeOrAfterSubmit_ShowsCancellationNotOldFeedback(bool started)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = started;
        var writes = 0;
        var feedbackCalls = 0;
        var page = new MutationConfirmationPage(Account, "Start", "target", "May incur charges.",
            () => { writes++; return pending.Task; },
            () => { feedbackCalls++; return ("Old result", null); },
            () => current);
        var form = Form(page);
        form.SubmitForm("{}", """{"action":"confirm"}""");
        current = false;
        pending.SetResult();
        await page.CurrentSubmission;
        Assert.AreEqual(started ? 1 : 0, writes);
        Assert.AreEqual(0, feedbackCalls);
        Assert.Contains("Cancelled", form.TemplateJson);
        Assert.DoesNotContain("Old result", form.TemplateJson);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task Cancel_DoesNotSubmitAndCannotBeFollowedByConfirmation()
    {
        var writes = 0;
        var page = new MutationConfirmationPage(Account, "Start", "target", "May incur charges.",
            () => { writes++; return Task.CompletedTask; }, () => ("Complete", null), () => true);
        var form = Form(page);
        form.SubmitForm("{}", """{"action":"cancel"}""");
        form.SubmitForm("{}", """{"action":"confirm"}""");
        await page.CurrentSubmission;
        Assert.AreEqual(0, writes);
        Assert.Contains("Cancelled", form.TemplateJson);
    }

    private static IFormContent Form(MutationConfirmationPage page) => (IFormContent)page.GetContent().Single();
}
