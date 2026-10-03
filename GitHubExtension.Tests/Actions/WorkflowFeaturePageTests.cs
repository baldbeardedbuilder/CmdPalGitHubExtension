// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Actions;

[TestClass]
public sealed class WorkflowFeaturePageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "token");

    [TestMethod]
    public async Task JobDetails_RequiresConfirmationAndWarnsAboutDependentJobs()
    {
        var job = Job();
        var client = new Mock<IActionsClient>();
        var reads = 0;
        client.Setup(c => c.GetJobAsync(Account, "octocat/hello", job.Id, It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Interlocked.Increment(ref reads) < 3
                ? job
                : job with { Status = "in_progress", Conclusion = null }));
        client.Setup(c => c.CanCancelAsync(Account, "octocat/hello", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.RerunJobAsync(Account, "octocat/hello", job.Id, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var auth = Auth();
        using var page = new WorkflowJobDetailsPage(auth, client.Object, new FakeBrowser(_ => null), "octocat/hello", Run(), job);

        page.GetContent();
        await page.CurrentOperation;
        Assert.Contains("restore: completed", Template(page));

        Submit(page, "rerun");
        Assert.Contains("Rerunning this job also reruns any jobs that depend on it", Template(page));
        client.Verify(c => c.RerunJobAsync(Account, "octocat/hello", job.Id, It.IsAny<CancellationToken>()), Times.Never);

        Submit(page, "confirm");
        await page.CurrentOperation;

        Assert.Contains("GitHub accepted the rerun request", Template(page));
        client.Verify(c => c.CanCancelAsync(Account, "octocat/hello", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.RerunJobAsync(Account, "octocat/hello", job.Id, It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task DownloadPage_CancelStopsTheActiveDownload()
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.DownloadAsync(Account, "octocat/hello", 7, null, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, long _, long? _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return Task.Delay(Timeout.Infinite, token);
            });
        using var page = new WorkflowDownloadPage(Auth(), client.Object, "octocat/hello", Run(7), null);

        Submit(page, "save", """{"destination":"C:\\temp\\logs.zip"}""");
        var token = await started.Task;
        Assert.Contains("Cancel download", Template(page));

        Submit(page, "cancel");
        await page.CurrentOperation;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.Contains("Download canceled. No partial file was saved.", Template(page));
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.DownloadAsync(Account, "octocat/hello", 7, null, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task JobRerun_AccountChangeDuringPermissionCheckPreventsMutation()
    {
        var job = Job();
        var permissionCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetJobAsync(Account, "octocat/hello", job.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        client.Setup(c => c.CanCancelAsync(Account, "octocat/hello", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                permissionCheck.SetResult();
                return await permissionResult.Task;
            });
        var auth = Auth();
        using var page = new WorkflowJobDetailsPage(auth, client.Object, new FakeBrowser(_ => null), "octocat/hello", Run(), job);

        page.GetContent();
        await page.CurrentOperation;
        Submit(page, "rerun");
        Submit(page, "confirm");
        await permissionCheck.Task;

        auth.SignOut();
        permissionResult.SetResult(true);
        await page.CurrentOperation;

        client.Verify(c => c.RerunJobAsync(
            It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task JobsPage_NextPageFailureKeepsPreviouslyLoadedJobsVisible()
    {
        var next = new Uri("https://api.github.com/repos/octocat/hello/actions/runs/7/jobs?page=2");
        var job = Job();
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetJobsAsync(Account, "octocat/hello", 7, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowJobsPageResult([job], next));
        client.Setup(c => c.GetJobsAsync(Account, "octocat/hello", 7, next, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("The next page is unavailable."));
        using var page = new WorkflowJobsPage(
            Auth(), client.Object, new FakeBrowser(_ => null), "octocat/hello", Run());

        page.GetItems();
        await page.CurrentLoad;
        page.LoadMore();
        await page.CurrentLoad;

        var items = page.GetItems();
        Assert.IsTrue(items.Any(item => item.Title == "build"));
        Assert.IsTrue(items.Any(item => item.Title == "Couldn't load workflow jobs"));
    }

    [TestMethod]
    public async Task Dispatch_AccountChangeDuringPermissionCheckPreventsMutation()
    {
        var permissionCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permissionResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = new GitHubWorkflow(12, "Deploy", ".github/workflows/deploy.yml", "active");
        var definition = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    type: string
            """);
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetDispatchContextAsync(Account, "octocat/hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowDispatchContext(["main"], "main"));
        client.Setup(c => c.GetWorkflowsAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPageResult([workflow], null));
        client.Setup(c => c.GetDispatchDefinitionAsync(
                Account, "octocat/hello", workflow.Path, "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        client.Setup(c => c.CanCancelAsync(Account, "octocat/hello", It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                permissionCheck.SetResult();
                return await permissionResult.Task;
            });
        var auth = Auth();
        using var page = new WorkflowDispatchPage(auth, client.Object, "octocat/hello");

        page.GetContent();
        await page.CurrentOperation;
        Submit(page, "load-inputs", """{"workflow":"12","dispatchRef":"main"}""");
        await page.CurrentOperation;
        Submit(page, "review", """{"workflow":"12","dispatchRef":"main","input_environment":"staging"}""");
        Submit(page, "dispatch");
        await permissionCheck.Task;

        auth.SignOut();
        permissionResult.SetResult(true);
        await page.CurrentOperation;

        client.Verify(c => c.DispatchAsync(
            It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(), It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Dispatch_UsesReviewedDefaultsAndRejectsDuplicateSubmission()
    {
        var definition = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  mode:
                    type: string
                    default: staging
            """);
        var client = DispatchClient(definition);
        client.Setup(c => c.DispatchAsync(Account, "octocat/hello", 12, "main",
                It.Is<Dictionary<string, string>>(values => values["mode"] == "staging"), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var page = new WorkflowDispatchPage(Auth(), client.Object, "octocat/hello");

        page.GetContent();
        await page.CurrentOperation;
        Submit(page, "load-inputs", """{"workflow":"12","dispatchRef":"main"}""");
        await page.CurrentOperation;
        Submit(page, "review", """{"workflow":"12","dispatchRef":"main"}""");
        Assert.Contains("staging (default)", Template(page));
        Submit(page, "dispatch");
        await page.CurrentOperation;
        Submit(page, "dispatch");

        Assert.Contains("GitHub accepted", Template(page));
        client.Verify(c => c.DispatchAsync(Account, "octocat/hello", 12, "main",
            It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Dispatch_ChangedDefaultRequiresAnotherReview()
    {
        var original = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  mode:
                    default: staging
            """);
        var changed = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  mode:
                    default: production
            """);
        var client = DispatchClient(original);
        client.SetupSequence(c => c.GetDispatchDefinitionAsync(Account, "octocat/hello",
                ".github/workflows/deploy.yml", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(original)
            .ReturnsAsync(changed);
        using var page = new WorkflowDispatchPage(Auth(), client.Object, "octocat/hello");

        page.GetContent();
        await page.CurrentOperation;
        Submit(page, "load-inputs", """{"workflow":"12","dispatchRef":"main"}""");
        await page.CurrentOperation;
        Submit(page, "review", """{"workflow":"12","dispatchRef":"main"}""");
        Submit(page, "dispatch");
        await page.CurrentOperation;

        Assert.Contains("inputs changed", Template(page));
        client.Verify(c => c.DispatchAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<long>(),
            It.IsAny<string>(), It.IsAny<Dictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static Mock<IActionsClient> DispatchClient(WorkflowDispatchDefinition definition)
    {
        var client = new Mock<IActionsClient>();
        client.Setup(c => c.GetWorkflowsAsync(Account, "octocat/hello", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowPageResult([new GitHubWorkflow(12, "Deploy", ".github/workflows/deploy.yml", "active")], null));
        client.Setup(c => c.GetDispatchContextAsync(Account, "octocat/hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowDispatchContext(["main"], "main"));
        client.Setup(c => c.GetDispatchDefinitionAsync(Account, "octocat/hello",
                ".github/workflows/deploy.yml", "main", It.IsAny<CancellationToken>()))
            .ReturnsAsync(definition);
        client.Setup(c => c.CanCancelAsync(Account, "octocat/hello", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return client;
    }

    private static GitHubAccount AuthAccount => Account;

    private static AuthService Auth() => new(
        new InMemoryAccountStore(AuthAccount),
        Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null),
        new OAuthOptions("id", "secret"));

    private static GitHubWorkflowJob Job() => new(
        17,
        "build",
        "completed",
        "failure",
        new Uri("https://github.com/octocat/hello/actions/runs/7"),
        [new GitHubWorkflowStep("restore", "completed", "success", 1)]);

    private static GitHubWorkflowRun Run(long id = 7) => new(
        id, "CI", "Build", "octocat", "completed", "failure",
        DateTimeOffset.UtcNow, new Uri($"https://github.com/octocat/hello/actions/runs/{id}"));

    private static ICommandResult Submit(ContentPage page, string action, string inputs = "{}") =>
        ((IFormContent)page.GetContent().Single()).SubmitForm(inputs, $$"""{"action":"{{action}}"}""");

    private static string Template(ContentPage page)
    {
        var template = ((IFormContent)page.GetContent().Single()).TemplateJson;
        using var json = JsonDocument.Parse(template);
        Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        return template;
    }
}
