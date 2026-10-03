// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Concurrent;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Pages;

[TestClass]
[DoNotParallelize]
public sealed class PageDiagnosticsTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Uri WebUrl = new("https://github.com/private/repository");
    private static AuthService CreateAuth() =>
        new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    [TestMethod]
    public async Task NotificationLoad_RestFailureIsCorrelatedAndNotLoggedTwiceAsError()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(
            new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"private response"}"""),
            })));
        var page = new NotificationsPage(CreateAuth(), new NotificationsClient(http), new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;

        var rest = entries.Single(e => e.Event == DiagnosticEvent.RestRequest && e.Outcome == DiagnosticOutcome.Failed);
        var load = entries.Last(e => e.Event == DiagnosticEvent.PageLoad && e.Outcome == DiagnosticOutcome.Failed);
        Assert.AreEqual(rest.OperationId, load.OperationId);
        Assert.AreEqual(DiagnosticArea.Notifications, rest.Area);
        Assert.AreEqual(DiagnosticSeverity.Information, load.Severity);
        Assert.HasCount(1, entries.Where(e => e.Severity == DiagnosticSeverity.Error));
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NotificationSubject_MissingDetailsProducesTypedSchemaFailure()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var api = new Uri("https://api.github.com/repos/private/repository/issues/1");
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [new GitHubNotification("1", "private title", "Issue", api, "private/repository", WebUrl, "mention", true, Now)], null));
        client.Setup(c => c.GetSubjectAsync(Account, api, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubjectDetails(SubjectState.Open, WebUrl));
        var page = new NotificationsPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;

        var failure = entries.Single(e => e.Event == DiagnosticEvent.SchemaRead && e.Outcome == DiagnosticOutcome.Failed);
        Assert.AreEqual(DiagnosticFailure.Schema, failure.Failure);
        Assert.AreEqual(DiagnosticArea.Notifications, failure.Area);
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task NotificationMutation_FailurePreservesItemAndShowsFeedback(bool done, bool ambiguous)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var client = new Mock<INotificationsClient>();
        var notification = new GitHubNotification("1", "private title", "Discussion", null, "private/repository", WebUrl, "mention", true, Now);
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([notification], null));
        var error = ambiguous ? (Exception)new HttpRequestException("private failure") : new GitHubApiException("private failure");
        client.Setup(c => c.MarkAsReadAsync(Account, "1", It.IsAny<CancellationToken>())).ThrowsAsync(error);
        client.Setup(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>())).ThrowsAsync(error);
        var page = new NotificationsPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single());
        if (done)
        {
            page.MarkAsDone(item);
        }
        else
        {
            page.MarkAsRead(item);
        }

        await page.CurrentMutation;
        Assert.IsTrue(item.Unread);
        Assert.Contains(item, page.GetItems());
        Assert.AreEqual("Couldn't update notification", page.GetItems().Last().Title);
        Assert.IsFalse(string.IsNullOrWhiteSpace(page.GetItems().Last().Subtitle));
        var outcome = entries.Single(e => e.Event == (done ? DiagnosticEvent.NotificationDone : DiagnosticEvent.NotificationRead)
            && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(ambiguous ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed, outcome.Outcome);
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));

        await page.RefreshAsync();
        Assert.HasCount(1, page.GetItems());
        Assert.AreNotEqual("Couldn't update notification", page.EmptyContent!.Title);
    }

    [TestMethod]
    public async Task NotificationMutation_StaleFailureDoesNotRestorePreviousAccountItem()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var auth = CreateAuth();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult(
                [new GitHubNotification("1", "private title", "Discussion", null, "private/repository", WebUrl, "mention", true, Now)], null));
        client.Setup(c => c.MarkAsDoneAsync(Account, "1", It.IsAny<CancellationToken>()))
            .Callback(() => started.SetResult())
            .Returns(pending.Task);
        using var page = new NotificationsPage(auth, client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        page.MarkAsDone(Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single()));
        var mutation = page.CurrentMutation;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        auth.SignOut();
        pending.SetException(new GitHubApiException("private failure"));
        await mutation;

        Assert.IsEmpty(page.GetItems());
        var outcome = entries.Single(e => e.Event == DiagnosticEvent.NotificationDone && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(DiagnosticOutcome.Cancelled, outcome.Outcome);
        Assert.AreEqual(DiagnosticSeverity.Information, outcome.Severity);
    }

    [TestMethod]
    [DataRow(false, 202)]
    [DataRow(true, 202)]
    [DataRow(false, 204)]
    [DataRow(true, 204)]
    public async Task NotificationMutation_PageInheritsAcceptedOrConfirmedDomainOutcome(bool done, int status)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var submitted = false;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.Method != HttpMethod.Get)
            {
                submitted = true;
                return Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status));
            }

            var completed = submitted && status == 204;
            var json = completed && done ? "[]" : $$"""
                [{"id":"1","subject":{"title":"private title","type":"Discussion","url":null},
                  "repository":{"full_name":"private/repository","html_url":"https://github.com/private/repository"},
                  "reason":"mention","unread":{{(!completed).ToString().ToLowerInvariant()}},"updated_at":"2025-06-01T12:00:00Z"}]
                """;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }));
        var browser = new FakeBrowser(_ => null);
        var page = new NotificationsPage(CreateAuth(), new NotificationsClient(http), browser);
        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<NotificationItem>(page.GetItems().Single());
        if (done)
        {
            page.MarkAsDone(item);
        }
        else
        {
            page.MarkAsRead(item);
        }

        await page.CurrentMutation;
        var outcome = entries.Last(e => e.Event == (done ? DiagnosticEvent.NotificationDone : DiagnosticEvent.NotificationRead)
            && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(status == 202 ? DiagnosticOutcome.Accepted : DiagnosticOutcome.Completed, outcome.Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CodespaceAction_StartCompletesAfterPollingWhileStopRemainsAccepted(bool start)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var client = new Mock<ICodespacesClient>();
        var codespace = new GitHubCodespace("private-name", "private title", "private/repository", "private-branch",
            start ? "Shutdown" : "Available", Now, WebUrl);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([codespace], null));
        client.Setup(c => c.StopCodespaceAsync(Account, codespace.Name, It.IsAny<CancellationToken>()))
            .Callback(() => codespace = codespace with { State = "ShuttingDown" })
            .ReturnsAsync(codespace with { State = "ShuttingDown" });
        client.Setup(c => c.StartCodespaceAsync(Account, codespace.Name, It.IsAny<CancellationToken>()))
            .Callback(() => codespace = codespace with { State = "Starting" })
            .ReturnsAsync(codespace with { State = "Starting" });
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new CodespacesPageResult([codespace], null));
        client.Setup(c => c.GetCodespaceAsync(Account, codespace.Name, It.IsAny<CancellationToken>()))
            .ReturnsAsync(codespace with { State = "Available" });
        using var page = new CodespacesPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        var item = Assert.IsInstanceOfType<CodespaceItem>(page.GetItems().Single());
        await (start ? page.StartAsync(item) : page.CloseAsync(item));

        var outcome = entries.Single(e => e.Event == (start ? DiagnosticEvent.CodespaceStart : DiagnosticEvent.CodespaceStop)
            && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(start ? DiagnosticOutcome.Completed : DiagnosticOutcome.Accepted, outcome.Outcome);
        client.Verify(c => c.GetCodespaceAsync(Account, codespace.Name, It.IsAny<CancellationToken>()),
            start ? Times.Once() : Times.Never());
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CodespaceCreate_ResponseIsAcceptedNotCompleted()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.CreateCodespaceAsync(Account, "private/repository", "private-branch", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GitHubCodespace("private-name", "private title", "private/repository", "private-branch", "Starting", Now, WebUrl));
        using var page = new CreateCodespacePage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        SubmitCreate(page, """{"repository":"private/repository","branch":"private-branch"}""");
        await page.CurrentCreate;

        var outcome = entries.Single(e => e.Event == DiagnosticEvent.CodespaceCreate && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(DiagnosticOutcome.Accepted, outcome.Outcome);
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CodespaceAction_DomainFailedStateIsNotReportedAsAccepted()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var client = new Mock<ICodespacesClient>();
        var codespace = new GitHubCodespace("private-name", "private title", "private/repository", "private-branch", "Shutdown", Now, WebUrl);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([codespace], null));
        client.Setup(c => c.StartCodespaceAsync(Account, codespace.Name, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                using var domain = OperationDiagnostics.Begin(DiagnosticEvent.CodespaceStart, DiagnosticArea.Codespaces);
                domain.Complete(DiagnosticOutcome.Failed);
                client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new CodespacesPageResult([codespace with { State = "Failed" }], null));
                return Task.FromResult(codespace with { State = "Failed" });
            });
        using var page = new CodespacesPage(CreateAuth(), client.Object, new FakeBrowser(_ => null));
        page.GetItems();
        await page.CurrentLoad;
        await page.StartAsync(Assert.IsInstanceOfType<CodespaceItem>(page.GetItems().Single()));

        var outcome = entries.Last(e => e.Event == DiagnosticEvent.CodespaceStart && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(DiagnosticOutcome.Failed, outcome.Outcome);
        Assert.HasCount(1, entries.Where(e => e.Severity == DiagnosticSeverity.Error));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CodespaceCreate_FailureBeforePostIsFailedAndSchemaAfterAcceptanceIsUnknown(bool sent)
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var requests = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            Interlocked.Increment(ref requests);
            if (request.Method == HttpMethod.Get)
            {
                return sent
                    ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"id":1}"""),
                    })
                    : Task.FromException<HttpResponseMessage>(new HttpRequestException("private transport failure"));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Accepted)
            {
                Content = new StringContent("private malformed response"),
            });
        }));
        using var page = new CreateCodespacePage(CreateAuth(), new CodespacesClient(http), new FakeBrowser(_ => null));
        SubmitCreate(page, """{"repository":"private/repository"}""");
        await page.CurrentCreate;

        var outcome = entries.Last(e => e.Event == DiagnosticEvent.CodespaceCreate && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(sent ? DiagnosticOutcome.Unknown : DiagnosticOutcome.Failed, outcome.Outcome);
        Assert.AreEqual(sent ? DiagnosticFailure.Schema : DiagnosticFailure.Transport, outcome.Failure);
        Assert.AreEqual(DiagnosticSeverity.Information, outcome.Severity);
        Assert.AreEqual(sent ? 2 : 1, requests);
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PageLoad_AccountResetDuringPublicationIsCancelledNotCompleted()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue, verboseReads: true);
        var auth = CreateAuth();
        var client = new Mock<INotificationsClient>();
        client.Setup(c => c.GetNotificationsAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NotificationsPageResult([], null));
        var page = new NotificationsPage(auth, client.Object, new FakeBrowser(_ => null));
        page.ItemsChanged += (_, _) =>
        {
            if (auth.CurrentAccount is not null)
            {
                auth.SignOut();
            }
        };
        page.GetItems();
        await page.CurrentLoad;
        var outcome = entries.Single(e => e.Event == DiagnosticEvent.PageLoad && e.Outcome != DiagnosticOutcome.Requested);
        Assert.AreEqual(DiagnosticOutcome.Cancelled, outcome.Outcome);
        Assert.AreEqual(DiagnosticSeverity.Information, outcome.Severity);
    }

    [TestMethod]
    public async Task RepositorySearch_CancellationIsInformationAndTimeoutIsFailed()
    {
        var entries = new ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue, verboseReads: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<RepositorySearchPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IRepositoriesClient>();
        client.Setup(c => c.SearchAsync(Account, "private-query", null, It.IsAny<CancellationToken>()))
            .Returns(() => { started.SetResult(); return pending.Task; });
        client.Setup(c => c.SearchAsync(Account, "timeout", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("private timeout"));
        var auth = CreateAuth();
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var pulls = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var page = new ReposPage(auth, client.Object, browser, issues, pulls, searchDelay: TimeSpan.Zero);
        page.SearchText = "private-query";
        var oldSearch = page.CurrentSearch;
        await started.Task;
        page.SearchText = "timeout";
        await page.CurrentSearch;
        pending.SetResult(new RepositorySearchPageResult([], null, 0));
        await oldSearch;

        var outcomes = entries.Where(e => e.Event == DiagnosticEvent.PageSearch && e.Outcome != DiagnosticOutcome.Requested).ToArray();
        Assert.HasCount(2, outcomes);
        Assert.AreEqual(DiagnosticSeverity.Information, outcomes.Single(e => e.Outcome == DiagnosticOutcome.Cancelled).Severity);
        Assert.AreEqual(DiagnosticFailure.Timeout, outcomes.Single(e => e.Outcome == DiagnosticOutcome.Failed).Failure);
        Assert.IsFalse(entries.Any(e => e.ToString().Contains("private", StringComparison.Ordinal)));
    }

    private static void SubmitCreate(CreateCodespacePage page, string inputs)
    {
        ((Microsoft.CommandPalette.Extensions.IFormContent)page.GetContent()[0])
            .SubmitForm(inputs, """{"action":"create"}""");
        ((Microsoft.CommandPalette.Extensions.IFormContent)page.GetContent()[0])
            .SubmitForm("{}", """{"action":"confirmCreate"}""");
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
