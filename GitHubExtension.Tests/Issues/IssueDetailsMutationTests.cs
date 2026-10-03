// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;
using static BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues.IssueMutationTestData;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueDetailsMutationTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("Open", "closeCompleted", "reopen")]
    [DataRow("Closed", "reopen", "closeCompleted")]
    [DataRow("NotPlanned", "reopen", "closeNotPlanned")]
    [DataRow("Unknown", "addLabel", "closeCompleted")]
    public void DetailsCard_OnlyOffersApplicableLifecycleActions(string state, string present, string absent)
    {
        var card = IssueDetailsCards.Details("octo/tool", Issue(Enum.Parse<SubjectState>(state)), true, login: Account.Login);
        using var json = JsonDocument.Parse(card);
        Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        Assert.Contains(present, card);
        Assert.DoesNotContain(absent, card);
    }

    [TestMethod]
    public async Task Close_RequiresConfirmationAndRefreshesDetails()
    {
        var read = Reader(Issue());
        var mutations = IssueMutationSessionTests.Client(Issue());
        var updated = Issue(SubjectState.NotPlanned) with { Title = "Fresh title" };
        mutations.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>())).ReturnsAsync(Issue()).ReturnsAsync(updated);
        mutations.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.NotPlanned, It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        using var page = new IssueDetailsPage(CreateAuth(), read.Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;

        Submit(page, IssueDetailsActions.CloseNotPlanned);
        var confirmation = Form(page);
        Assert.Contains("Account: octocat", confirmation.TemplateJson);
        Assert.Contains("Host: https://github.com/", confirmation.TemplateJson);
        Assert.Contains("octo/tool#42", confirmation.TemplateJson);
        mutations.Verify(c => c.ChangeStateAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SubjectState>(), It.IsAny<CancellationToken>()), Times.Never);
        confirmation.SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.Confirm}}"}""");
        confirmation.SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.Confirm}}"}""");
        await page.CurrentMutation;

        Assert.Contains("Fresh title", Form(page).TemplateJson);
        Assert.Contains("NOT PLANNED", Form(page).TemplateJson);
        Assert.Contains("Issue updated.", Form(page).TemplateJson);
        Assert.DoesNotContain(IssueDetailsActions.CloseCompleted, Form(page).TemplateJson);
        mutations.Verify(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.NotPlanned, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Cancel_DoesNotWriteAndOldConfirmationCannotSubmit()
    {
        var mutations = IssueMutationSessionTests.Client(Issue());
        using var page = new IssueDetailsPage(CreateAuth(), Reader(Issue()).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.CloseCompleted);
        var old = Form(page);
        old.SubmitForm("{}", """{"action":"cancel"}""");
        old.SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.Confirm}}"}""");
        await page.CurrentMutation;

        Assert.Contains(IssueDetailsActions.CloseCompleted, Form(page).TemplateJson);
        mutations.Verify(c => c.ChangeStateAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SubjectState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow("assignSelf", true)]
    [DataRow("removeSelf", false)]
    public async Task SelfAssignment_AddsOrRemovesOnlyCurrentLogin(string action, bool add)
    {
        var before = Issue(assignees: add ? ["mona"] : ["mona", "octocat"]);
        var after = before with { Assignees = add ? ["mona", "octocat"] : ["mona"] };
        var mutations = IssueMutationSessionTests.Client(before);
        mutations.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>())).ReturnsAsync(before).ReturnsAsync(after);
        mutations.Setup(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", add, It.IsAny<CancellationToken>())).ReturnsAsync(after);
        using var page = new IssueDetailsPage(CreateAuth(), Reader(before).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;

        Submit(page, action);
        Submit(page, IssueDetailsActions.Confirm);
        await page.CurrentMutation;

        Assert.Contains("mona", Form(page).TemplateJson);
        Assert.Contains(add ? IssueDetailsActions.RemoveSelf : IssueDetailsActions.AssignSelf, Form(page).TemplateJson);
        mutations.Verify(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", add, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("addAssignee", "octocat", true, true)]
    [DataRow("removeAssignee", "mona", false, true)]
    [DataRow("addLabel", "help wanted", true, false)]
    [DataRow("removeLabel", "bug", false, false)]
    public async Task Picker_ReviewsAnExistingSelectionAndPreservesOtherMetadata(string action, string selection, bool add, bool assignee)
    {
        var before = Issue();
        var after = assignee
            ? before with { Assignees = add ? ["mona", "octocat"] : [] }
            : before with { Labels = add ? ["bug", "help wanted"] : [] };
        var mutations = IssueMutationSessionTests.Client(before);
        mutations.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(before).ReturnsAsync(after).ReturnsAsync(after);
        mutations.Setup(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, selection, add, It.IsAny<CancellationToken>())).ReturnsAsync(after);
        mutations.Setup(c => c.ChangeLabelAsync(Account, "octo/tool", 42, selection, add, It.IsAny<CancellationToken>())).ReturnsAsync(after.Labels);
        using var page = new IssueDetailsPage(CreateAuth(), Reader(before).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;

        Submit(page, action);
        await page.CurrentLoad;
        using (var json = JsonDocument.Parse(Form(page).TemplateJson))
        {
            var picker = json.RootElement.GetProperty("body").EnumerateArray().Single(item =>
                item.GetProperty("type").GetString() == "Input.ChoiceSet");
            Assert.AreEqual(selection, picker.GetProperty("choices")[0].GetProperty("title").GetString());
        }

        Submit(page, IssueDetailsActions.Select, """{"selection":"0"}""");
        Assert.Contains(selection, Form(page).TemplateJson);
        Submit(page, IssueDetailsActions.Confirm);
        await page.CurrentMutation;
        Assert.Contains("Issue updated.", Form(page).TemplateJson);
        if (assignee)
        {
            mutations.Verify(c => c.ChangeAssigneeAsync(Account, "octo/tool", 42, selection, add, It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            mutations.Verify(c => c.ChangeLabelAsync(Account, "octo/tool", 42, selection, add, It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [TestMethod]
    [DataRow("""{"selection":"999"}""")]
    [DataRow("""{"selection":"-1"}""")]
    [DataRow("""{"selection":"help wanted"}""")]
    [DataRow("""{"selection":0}""")]
    [DataRow("[]")]
    [DataRow("broken")]
    public async Task Picker_InvalidInputNeverWrites(string inputs)
    {
        var mutations = IssueMutationSessionTests.Client(Issue());
        using var page = new IssueDetailsPage(CreateAuth(), Reader(Issue()).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.AddLabel);
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.Select, inputs);
        Submit(page, IssueDetailsActions.Confirm);

        Assert.Contains("No request was sent.", Form(page).TemplateJson);
        mutations.Verify(c => c.ChangeLabelAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task StaleFormOrAccount_CannotMutateNewIssue()
    {
        var mutations = IssueMutationSessionTests.Client(Issue());
        using var auth = CreateAuth();
        using var page = new IssueDetailsPage(auth, Reader(Issue()).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.CloseCompleted);
        var oldConfirmation = Form(page);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;
        oldConfirmation.SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.Confirm}}"}""");
        Submit(page, IssueDetailsActions.CloseCompleted);
        var accountConfirmation = Form(page);
        await auth.SignInWithTokenAsync("https://github.com", "replacement", TestContext.CancellationToken);
        accountConfirmation.SubmitForm("{}", $$"""{"action":"{{IssueDetailsActions.Confirm}}"}""");

        Assert.Contains("Sign in", Form(page).TemplateJson);
        mutations.Verify(c => c.ChangeStateAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<SubjectState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [Timeout(10000)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DisposeOrAccountChange_CancelsWriteAndDoesNotPublishLateSuccess(bool dispose)
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<GitHubIssue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = IssueMutationSessionTests.Client(Issue());
        mutations.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, int _, SubjectState _, CancellationToken token) =>
            {
                started.SetResult(token);
                return finish.Task;
            });
        using var auth = CreateAuth();
        using var page = new IssueDetailsPage(auth, Reader(Issue()).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.CloseCompleted);
        Submit(page, IssueDetailsActions.Confirm);
        var operation = page.CurrentMutation;
        var token = await started.Task.WaitAsync(TestContext.CancellationToken);
        if (dispose)
        {
            page.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        await operation;
        finish.SetResult(Issue(SubjectState.Closed));
        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(page.IsLoading);
        Assert.DoesNotContain("Issue updated.", Form(page).TemplateJson);
        Assert.Contains("Sign in", Form(page).TemplateJson);
    }

    [TestMethod]
    public async Task PickerFailure_ShowsSsoLinkWithoutLosingIssue()
    {
        var mutations = IssueMutationSessionTests.Client(Issue());
        var authorize = new Uri("https://github.com/orgs/octo/sso");
        mutations.Setup(c => c.GetLabelsAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Authorize access.", authorizeUrl: authorize));
        using var page = new IssueDetailsPage(CreateAuth(), Reader(Issue()).Object, new FakeBrowser(_ => null), mutations.Object);
        page.LoadIssue(Account, ApiUrl, "octo/tool");
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.AddLabel);
        await page.CurrentLoad;

        Assert.Contains("Keyboard navigation", Form(page).TemplateJson);
        Assert.Contains("Authorize access.", Form(page).TemplateJson);
        Assert.Contains(authorize.AbsoluteUri, Form(page).TemplateJson);
    }

    [TestMethod]
    public async Task RepositoryMutation_UpdatesFilteredListAndDoesNotNotifyUnderLock()
    {
        var before = Issue();
        var after = Issue(SubjectState.Closed) with { Labels = ["bug", "updated"] };
        var reader = new Mock<IIssuesClient>();
        var mutations = reader.As<IIssueMutationsClient>();
        reader.Setup(c => c.GetIssuesAsync(Account, "octo/tool", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssuesPageResult([before], null));
        reader.Setup(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>())).ReturnsAsync(before);
        mutations.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(before).ReturnsAsync(after);
        mutations.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>())).ReturnsAsync(after);
        using var repository = new RepositoryIssuesPage(CreateAuth(), reader.Object, new FakeBrowser(_ => null));
        repository.Open("octo/tool");
        await repository.CurrentLoad;
        var detail = Assert.IsInstanceOfType<IssueDetailsPage>(Assert.ContainsSingle(repository.GetItems()).Command);
        detail.GetContent();
        await detail.CurrentLoad;
        repository.ItemsChanged += (_, _) => Assert.IsTrue(Task.Run(() => repository.GetItems()).Wait(TimeSpan.FromSeconds(2)));
        Submit(detail, IssueDetailsActions.CloseCompleted);
        Submit(detail, IssueDetailsActions.Confirm);
        await detail.CurrentMutation;

        Assert.IsEmpty(repository.GetItems());
        repository.Filters!.CurrentFilterId = IssueFilters.Closed;
        var item = Assert.IsInstanceOfType<RepositoryIssueItem>(Assert.ContainsSingle(repository.GetItems()));
        Assert.AreEqual(SubjectState.Closed, item.Issue.State);
        Assert.Contains("updated", item.Issue.Labels);
        Assert.AreSame(detail, item.Command);
        Assert.IsInstanceOfType<OpenInBrowserCommand>(Assert.IsInstanceOfType<CommandContextItem>(item.MoreCommands[0]).Command);
    }

    [TestMethod]
    public async Task NotificationMutation_InvokesRefreshCallbackOnlyOnConfirmedSuccess()
    {
        var mutations = IssueMutationSessionTests.Client(Issue());
        var updated = Issue(SubjectState.Closed);
        mutations.SetupSequence(c => c.GetMutationIssueAsync(Account, "octo/tool", 42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Issue()).ReturnsAsync(updated);
        mutations.Setup(c => c.ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, It.IsAny<CancellationToken>())).ReturnsAsync(updated);
        using var template = new IssueDetailsPage(CreateAuth(), Reader(Issue()).Object, new FakeBrowser(_ => null), mutations.Object);
        var opened = 0;
        GitHubIssue? changed = null;
        using var page = template.ForNotification("thread", ApiUrl, "octo/tool", () => opened++, issue => changed = issue);
        page.GetContent();
        await page.CurrentLoad;
        Submit(page, IssueDetailsActions.CloseCompleted);
        Submit(page, IssueDetailsActions.Confirm);
        await page.CurrentMutation;

        Assert.AreEqual(1, opened);
        Assert.AreEqual(updated, changed);
    }

    private static Mock<IIssuesClient> Reader(GitHubIssue issue)
    {
        var client = new Mock<IIssuesClient>();
        client.Setup(c => c.GetIssueAsync(Account, ApiUrl, It.IsAny<CancellationToken>())).ReturnsAsync(issue);
        return client;
    }

    private static FormContent Form(IssueDetailsPage page) => Assert.IsInstanceOfType<FormContent>(Assert.ContainsSingle(page.GetContent()));
    private static void Submit(IssueDetailsPage page, string action, string inputs = "{}") =>
        Form(page).SubmitForm(inputs, $$"""{"action":"{{action}}"}""");
}
