// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using static BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues.IssueMutationTestData;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueFeaturePageTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task FailedCommentWrite_RetainsUserDraftForRetry()
    {
        var client = new Mock<IIssueConversationClient>();
        client.Setup(c => c.GetCommentsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage([], null));
        client.Setup(c => c.CreateCommentAsync(Account, "octo/tool", 42, "comment draft", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Write denied."));
        using var page = new IssueConversationPage(CreateAuth(), client.Object, Account, "octo/tool", 42, "Issue");

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"body":"comment draft"}""", """{"action":"post"}""");
        await page.CurrentWork;

        using var card = JsonDocument.Parse(((IFormContent)page.GetContent().Single()).TemplateJson);
        var input = card.RootElement.GetProperty("body").EnumerateArray()
            .Single(item => item.TryGetProperty("id", out var id) && id.GetString() == "body");
        Assert.AreEqual("comment draft", input.GetProperty("value").GetString());
        Assert.Contains("Write denied.", card.RootElement.GetRawText());
    }

    [TestMethod]
    public async Task StaleExternalGeneration_DoesNotLoadOrPostComment()
    {
        var current = true;
        var client = new Mock<IIssueConversationClient>();
        client.Setup(c => c.GetCommentsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage([], null));
        using var page = new IssueConversationPage(CreateAuth(), client.Object, Account, "octo/tool", 42, "Issue",
            isCurrent: () => current);

        page.GetContent();
        await page.CurrentWork;
        current = false;
        Submit(page, """{"body":"comment draft"}""", """{"action":"post"}""");
        await page.CurrentWork;

        client.Verify(c => c.CreateCommentAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.GetCommentsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 42, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ExternalGenerationChangeDuringCommentWrite_SuppressesRefresh()
    {
        var current = true;
        var client = new Mock<IIssueConversationClient>();
        client.Setup(c => c.GetCommentsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage([], null));
        client.Setup(c => c.CreateCommentAsync(Account, "octo/tool", 42, "comment draft", It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                current = false;
                return Task.FromResult(new IssueComment(1, "comment draft", "octocat", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null));
            });
        using var page = new IssueConversationPage(CreateAuth(), client.Object, Account, "octo/tool", 42, "Issue",
            isCurrent: () => current);

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"body":"comment draft"}""", """{"action":"post"}""");
        await page.CurrentWork;

        client.Verify(c => c.CreateCommentAsync(Account, "octo/tool", 42, "comment draft", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCommentsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 42, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SuccessfulCommentWriteWithFailedRefresh_ClearsDraftAndPreventsDuplicate()
    {
        var client = new Mock<IIssueConversationClient>();
        client.SetupSequence(c => c.GetCommentsAsync(Account, "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage([], null))
            .ThrowsAsync(new GitHubApiException("Refresh failed."));
        client.Setup(c => c.CreateCommentAsync(Account, "octo/tool", 42, "posted comment", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueComment(12, "posted comment", Account.Login, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                new Uri("https://api.github.com/repos/octo/tool/issues/42")));
        using var page = new IssueConversationPage(CreateAuth(), client.Object, Account, "octo/tool", 42, "Issue");

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"body":"posted comment"}""", """{"action":"post"}""");
        await page.CurrentWork;

        using var card = JsonDocument.Parse(((IFormContent)page.GetContent().Single()).TemplateJson);
        var input = card.RootElement.GetProperty("body").EnumerateArray()
            .Single(item => item.TryGetProperty("id", out var id) && id.GetString() == "body");
        Assert.AreEqual(string.Empty, input.GetProperty("value").GetString());
        Assert.Contains("Comment saved, but comments could not be refreshed", card.RootElement.GetRawText());
        client.Verify(c => c.CreateCommentAsync(Account, "octo/tool", 42, "posted comment", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CancelCommentDeletionDoesNotCallClient()
    {
        var client = new Mock<IIssueConversationClient>();
        client.Setup(c => c.GetCommentsAsync(Account, "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage(
                [new IssueComment(12, "Keep this comment", Account.Login, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new Uri("https://api.github.com/repos/octo/tool/issues/42"))], null));
        using var page = new IssueConversationPage(CreateAuth(), client.Object, Account, "octo/tool", 42, "Issue");

        page.GetContent();
        await page.CurrentWork;
        Submit(page, "{}", """{"action":"delete","id":"12"}""");
        Assert.Contains("Delete this issue comment?", ((IFormContent)page.GetContent().Single()).TemplateJson);

        Submit(page, "{}", """{"action":"cancel-delete"}""");

        var card = ((IFormContent)page.GetContent().Single()).TemplateJson;
        Assert.Contains("Keep this comment", card);
        Assert.DoesNotContain("Confirm delete", card);
        client.Verify(c => c.DeleteCommentAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(),
            It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task FailedIssueCreate_RetainsDraftAfterConfirmationFailure()
    {
        var client = new Mock<IIssueManagementClient>();
        client.Setup(c => c.GetMilestonesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueMilestonesPage([], null));
        client.Setup(c => c.CreateIssueAsync(Account, "octo/tool", "Issue draft", "Description draft", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Create denied."));
        using var page = new IssueWritePage(CreateAuth(), client.Object, "octo/tool");

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"title":"Issue draft","body":"Description draft","milestone":"0"}""", """{"action":"review"}""");
        Submit(page, "{}", """{"action":"confirm"}""");
        await page.CurrentWork;

        using var card = JsonDocument.Parse(((IFormContent)page.GetContent().Single()).TemplateJson);
        var inputs = card.RootElement.GetProperty("body").EnumerateArray()
            .Where(item => item.TryGetProperty("id", out _)).ToArray();
        Assert.AreEqual("Issue draft", inputs.Single(item => item.GetProperty("id").GetString() == "title").GetProperty("value").GetString());
        Assert.AreEqual("Description draft", inputs.Single(item => item.GetProperty("id").GetString() == "body").GetProperty("value").GetString());
        Assert.Contains("Create denied.", card.RootElement.GetRawText());
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task AccountSwitchDuringCommentWrite_CancelsAndSuppressesOldResult()
    {
        var client = new Mock<IIssueConversationClient>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<IssueComment>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetCommentsAsync(It.IsAny<GitHubAccount>(), "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage([], null));
        client.Setup(c => c.CreateCommentAsync(Account, "octo/tool", 42, "comment draft", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, int _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return finish.Task;
            });
        using var auth = CreateAuth();
        using var page = new IssueConversationPage(auth, client.Object, Account, "octo/tool", 42, "Issue");

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"body":"comment draft"}""", """{"action":"post"}""");
        var token = await started.Task.WaitAsync(TestContext.CancellationToken);

        await auth.SignInWithTokenAsync("https://github.com", "replacement-token", TestContext.CancellationToken);
        Assert.IsTrue(token.IsCancellationRequested);
        finish.SetResult(new IssueComment(13, "comment draft", Account.Login, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, new Uri("https://api.github.com/repos/octo/tool/issues/42")));
        await page.CurrentWork;

        page.GetContent();
        await page.CurrentWork;
        client.Verify(c => c.GetCommentsAsync(Account, "octo/tool", 42, null, It.IsAny<CancellationToken>()), Times.Once);
        using var card = JsonDocument.Parse(((IFormContent)page.GetContent().Single()).TemplateJson);
        Assert.DoesNotContain("comment draft", card.RootElement.GetRawText());
    }

    [TestMethod]
    [Timeout(10000)]
    public async Task AccountSwitchDuringIssueCreate_CancelsAndSuppressesRefresh()
    {
        var client = new Mock<IIssueManagementClient>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetMilestonesAsync(It.IsAny<GitHubAccount>(), "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueMilestonesPage([], null));
        client.Setup(c => c.CreateIssueAsync(Account, "octo/tool", "Issue draft", "Description draft", null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, string _, string? _, int? _, CancellationToken token) =>
            {
                started.SetResult(token);
                return finish.Task;
            });
        using var auth = CreateAuth();
        var created = false;
        using var page = new IssueWritePage(auth, client.Object, "octo/tool", created: _ =>
        {
            created = true;
            return Task.CompletedTask;
        });

        page.GetContent();
        await page.CurrentWork;
        Submit(page, """{"title":"Issue draft","body":"Description draft","milestone":"0"}""", """{"action":"review"}""");
        Submit(page, "{}", """{"action":"confirm"}""");
        var token = await started.Task.WaitAsync(TestContext.CancellationToken);

        await auth.SignInWithTokenAsync("https://github.com", "replacement-token", TestContext.CancellationToken);
        Assert.IsTrue(token.IsCancellationRequested);
        finish.SetResult(Issue());
        await page.CurrentWork;

        page.GetContent();
        await page.CurrentWork;
        Assert.IsFalse(created);
        using var card = JsonDocument.Parse(((IFormContent)page.GetContent().Single()).TemplateJson);
        Assert.Contains("Save", card.RootElement.GetRawText());
    }

    private static void Submit(ContentPage page, string inputs, string data) =>
        ((IFormContent)page.GetContent().Single()).SubmitForm(inputs, data);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancelIssueEditor_ReturnsWithoutSavingOrReusingConfirmation(bool reviewing)
    {
        var client = new Mock<IIssueManagementClient>();
        client.Setup(c => c.GetMilestonesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueMilestonesPage([], null));
        using var auth = CreateAuth();
        using var page = new IssueWritePage(auth, client.Object, "octo/tool");
        page.GetContent();
        await page.CurrentWork;
        if (reviewing)
            Submit(page, """{"title":"Unsaved issue","body":"Draft","milestone":"0"}""", """{"action":"review"}""");

        var form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var actions = CardActions(form.TemplateJson);
        var cancel = Assert.ContainsSingle(actions.Where(action => action.GetProperty("title").GetString() == "Cancel"));
        Assert.AreEqual("none", cancel.GetProperty("associatedInputs").GetString());
        var result = form.SubmitForm("{}", cancel.GetProperty("data").GetRawText());
        form.SubmitForm("{}", """{"action":"confirm"}""");

        Assert.AreEqual(CommandResultKind.GoBack, result.Kind);
        client.Verify(c => c.CreateIssueAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.DoesNotContain("Unsaved issue", Assert.ContainsSingle(page.GetContent().OfType<IFormContent>()).TemplateJson);
    }

    [TestMethod]
    public async Task IssueEditor_SaveOpensReviewBeforeWriting()
    {
        var client = new Mock<IIssueManagementClient>();
        client.Setup(c => c.GetMilestonesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueMilestonesPage([], null));
        using var auth = CreateAuth();
        using var page = new IssueWritePage(auth, client.Object, "octo/tool");
        page.GetContent();
        await page.CurrentWork;
        var form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var save = Assert.ContainsSingle(CardActions(form.TemplateJson)
            .Where(action => action.GetProperty("title").GetString() == "Save"));
        Assert.AreEqual("auto", save.GetProperty("associatedInputs").GetString());

        form.SubmitForm("""{"title":"New issue","body":"Description","milestone":"0"}""", save.GetProperty("data").GetRawText());

        var review = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var actions = CardActions(review.TemplateJson);
        Assert.Contains("Create issue", actions.Select(action => action.GetProperty("title").GetString()));
        Assert.Contains("Cancel", actions.Select(action => action.GetProperty("title").GetString()));
        client.Verify(c => c.CreateIssueAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Conversation_EditAndDeletePreserveLargeCommentId()
    {
        const long id = 4_123_456_789;
        var comment = new IssueComment(id, "Old comment", Account.Login, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            ApiUrl);
        var client = new Mock<IIssueConversationClient>();
        client.Setup(c => c.GetCommentsAsync(Account, "octo/tool", 42, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueCommentsPage([comment], null));
        client.Setup(c => c.EditCommentAsync(Account, "octo/tool", 42, id, "Edited comment", It.IsAny<CancellationToken>()))
            .ReturnsAsync(comment with { Body = "Edited comment" });
        client.Setup(c => c.DeleteCommentAsync(Account, "octo/tool", 42, id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var auth = CreateAuth();
        using var page = new IssueConversationPage(auth, client.Object, Account, "octo/tool", 42, "Pull request",
            icon: Icons.StateMerged);
        page.GetContent();
        await page.CurrentWork;
        Assert.AreSame(Icons.StateMerged, page.Icon);
        var form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var edit = Assert.ContainsSingle(CardActions(form.TemplateJson)
            .Where(action => action.GetProperty("title").GetString() == "Edit"));
        form.SubmitForm("{}", edit.GetProperty("data").GetRawText());
        form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var save = Assert.ContainsSingle(CardActions(form.TemplateJson)
            .Where(action => action.GetProperty("title").GetString() == "Save comment"));
        Assert.AreEqual("auto", save.GetProperty("associatedInputs").GetString());
        form.SubmitForm("""{"body":"Edited comment"}""", save.GetProperty("data").GetRawText());
        await page.CurrentWork;

        form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var delete = Assert.ContainsSingle(CardActions(form.TemplateJson)
            .Where(action => action.GetProperty("title").GetString() == "Delete"));
        Assert.AreEqual("none", delete.GetProperty("associatedInputs").GetString());
        form.SubmitForm("{}", delete.GetProperty("data").GetRawText());
        form = Assert.ContainsSingle(page.GetContent().OfType<IFormContent>());
        var confirm = Assert.ContainsSingle(CardActions(form.TemplateJson)
            .Where(action => action.GetProperty("title").GetString() == "Confirm delete"));
        form.SubmitForm("{}", confirm.GetProperty("data").GetRawText());
        await page.CurrentWork;

        client.Verify(c => c.EditCommentAsync(Account, "octo/tool", 42, id, "Edited comment", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.DeleteCommentAsync(Account, "octo/tool", 42, id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void ConversationCards_AllButtonsAreValidActionsAndOnlySavesValidateInputs()
    {
        var comment = new IssueComment(4_123_456_789, "Comment", Account.Login, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ApiUrl);
        foreach (var editing in new long?[] { null, comment.Id })
        {
            var card = IssueConversationCards.Comments("Issue", "octo/tool", 42, [comment], Account.Login, "Draft",
                editing, true, null, null, false);
            var actions = CardActions(card);
            Assert.Contains(editing is null ? "Post comment" : "Save comment",
                actions.Select(action => action.GetProperty("title").GetString()));
            Assert.Contains("Refresh comments", actions.Select(action => action.GetProperty("title").GetString()));
            Assert.Contains("Load more comments", actions.Select(action => action.GetProperty("title").GetString()));
            foreach (var action in actions)
            {
                var operation = action.GetProperty("data").GetProperty("action").GetString();
                Assert.AreEqual(operation is "post" or "save-edit" ? "auto" : "none",
                    action.GetProperty("associatedInputs").GetString());
            }
        }
        var confirmation = CardActions(IssueConversationCards.ConfirmDelete("Issue", "octo/tool", 42, comment, null, null));
        Assert.HasCount(2, confirmation);
    }

    private static JsonElement[] CardActions(string template)
    {
        using var card = JsonDocument.Parse(template);
        var body = card.RootElement.GetProperty("body").EnumerateArray().ToArray();
        Assert.IsFalse(body.Any(item => item.GetProperty("type").GetString() is "Action.Submit" or "Action.OpenUrl"),
            "Actions must be contained in ActionSet, not the card body.");
        return body.Where(item => item.GetProperty("type").GetString() == "ActionSet")
            .SelectMany(item => item.GetProperty("actions").EnumerateArray()).Select(action => action.Clone()).ToArray();
    }
}
