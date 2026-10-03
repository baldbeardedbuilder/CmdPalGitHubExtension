// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Agents;

[TestClass]
public sealed class CreateAgentTaskPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly GitHubRepository Repository = new(
        "octocat/hello", new Uri("https://github.com/octocat/hello"), null, false, false, false,
        "C#", 0, 0, DateTimeOffset.UtcNow, null);

    [TestMethod]
    public void Form_RequiresReviewBeforeTaskCanBeSubmitted()
    {
        var client = new Mock<IAgentsClient>();
        using var page = CreatePage(client.Object, out _);

        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Fix a bug"}""");

        Assert.Contains("Review before starting", CurrentTemplate(page));
        Assert.Contains("Copilot cloud agent compute", CurrentTemplate(page));
        client.Verify(c => c.GetRepositoryTasksAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.StartTaskAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(),
            It.IsAny<AgentTaskRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Confirm_StartsTaskOnceAndShowsCreatedTask()
    {
        var client = new Mock<IAgentsClient>();
        client.Setup(c => c.GetRepositoryTasksAsync(Account, Repository.FullName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubAgentTask>());
        client.Setup(c => c.StartTaskAsync(Account, Repository.FullName,
                It.Is<AgentTaskRequest>(draft => draft.Prompt == "Fix a bug"
                    && draft.Model == "gpt-5.4"
                    && draft.CustomAgent == "reviewer"
                    && draft.BaseRef == "main"
                    && draft.HeadRef == "feature/fix"
                    && draft.CreatePullRequest),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(AgentTask("created"));
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateAgentTaskActions.Review,
            """{"prompt":"Fix a bug","model":"gpt-5.4","customAgent":"reviewer","baseRef":"main","headRef":"feature/fix","createPullRequest":"true"}""");

        Submit(page, CreateAgentTaskActions.Start);
        await page.CurrentOperation;

        Assert.Contains("Copilot task started", CurrentTemplate(page));
        Assert.Contains("Open task on GitHub", CurrentTemplate(page));
        client.Verify(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task FailedSubmissionKeepsDraftAndDisplaysPermissionError()
    {
        var client = new Mock<IAgentsClient>();
        client.Setup(c => c.GetRepositoryTasksAsync(Account, Repository.FullName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubAgentTask>());
        client.Setup(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Agent tasks: read and write permission is required."));
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Fix a bug","model":"my-model"}""");

        Submit(page, CreateAgentTaskActions.Start);
        await page.CurrentOperation;

        var template = CurrentTemplate(page);
        Assert.Contains("Agent tasks: read and write permission is required.", template);
        Assert.Contains("Fix a bug", template);
        Assert.Contains("my-model", template);
    }

    [TestMethod]
    public async Task UnknownOutcome_ReconcilesNewTaskAndNeverRetriesAutomatically()
    {
        var client = new Mock<IAgentsClient>();
        var existing = AgentTask("before");
        var appeared = AgentTask("possibly-created");
        client.SetupSequence(c => c.GetRepositoryTasksAsync(Account, Repository.FullName, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing])
            .ReturnsAsync([existing, appeared])
            .ReturnsAsync([existing, appeared]);
        client.Setup(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AgentTaskOutcomeUnknownException("response lost"));
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Fix a bug"}""");

        Submit(page, CreateAgentTaskActions.Start);
        await page.CurrentOperation;

        Assert.Contains("possibly-created", CurrentTemplate(page));
        client.Verify(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Submit(page, CreateAgentTaskActions.Check);
        await page.CurrentOperation;
        client.Verify(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);

        Submit(page, CreateAgentTaskActions.Edit);
        Assert.Contains("Fix a bug", CurrentTemplate(page));
        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Fix a bug"}""");
        Assert.Contains("A previous submission may still be running", CurrentTemplate(page));
        Assert.Contains("Start another task", CurrentTemplate(page));
    }

    [TestMethod]
    public async Task RepeatedConfirmationWhileRequestIsPending_DoesNotSubmitTwice()
    {
        var client = new Mock<IAgentsClient>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<GitHubAgentTask>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetRepositoryTasksAsync(Account, Repository.FullName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubAgentTask>());
        client.Setup(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.SetResult();
                return result.Task;
            });
        using var page = CreatePage(client.Object, out _);
        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Fix a bug"}""");

        Submit(page, CreateAgentTaskActions.Start);
        await started.Task;
        Submit(page, CreateAgentTaskActions.Start);
        result.SetResult(AgentTask("created"));
        await page.CurrentOperation;

        client.Verify(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task AccountSwitchWhileSubmitting_CancelsWorkAndHidesOldDraft()
    {
        var client = new Mock<IAgentsClient>();
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetRepositoryTasksAsync(Account, Repository.FullName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<GitHubAgentTask>());
        client.Setup(c => c.StartTaskAsync(Account, Repository.FullName, It.IsAny<AgentTaskRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, AgentTaskRequest _, CancellationToken token) =>
            {
                started.SetResult(token);
                await System.Threading.Tasks.Task.Delay(Timeout.Infinite, token);
                return AgentTask("created");
            });
        using var page = CreatePage(client.Object, out var auth);
        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Private draft for old account"}""");

        Submit(page, CreateAgentTaskActions.Start);
        var token = await started.Task;
        auth.SignOut();
        await page.CurrentOperation;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.DoesNotContain("Private draft for old account", CurrentTemplate(page));
        Assert.DoesNotContain("Copilot task started", CurrentTemplate(page));
    }

    [TestMethod]
    public void AccountSwitch_ClearsRetainedDraft()
    {
        var client = new Mock<IAgentsClient>();
        using var page = CreatePage(client.Object, out var auth);
        Submit(page, CreateAgentTaskActions.Review, """{"prompt":"Do not show this to another account"}""");

        auth.SignOut();

        Assert.DoesNotContain("Do not show this to another account", CurrentTemplate(page));
    }

    [TestMethod]
    public void Review_RejectsAnEmptyPrompt()
    {
        using var page = CreatePage(Mock.Of<IAgentsClient>(), out _);

        Submit(page, CreateAgentTaskActions.Review,
            """{"prompt":"  "}""");

        Assert.Contains("Enter a prompt for the agent.", CurrentTemplate(page));
    }

    private static GitHubAgentTask AgentTask(string id) =>
        new(id, "Fix a bug", new Uri($"https://github.com/copilot/tasks/{id}"), "queued",
            DateTimeOffset.UtcNow, null);

    private static CreateAgentTaskPage CreatePage(IAgentsClient client, out AuthService auth)
    {
        auth = new AuthService(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
            new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        return new CreateAgentTaskPage(auth, client, Repository);
    }

    private static ICommandResult Submit(CreateAgentTaskPage page, string action, string inputs = "{}") =>
        ((IFormContent)page.GetContent()[0]).SubmitForm(inputs, $$"""{"action":"{{action}}"}""");

    private static string CurrentTemplate(CreateAgentTaskPage page)
    {
        var template = ((IFormContent)page.GetContent()[0]).TemplateJson;
        using var json = JsonDocument.Parse(template);
        Assert.AreEqual("AdaptiveCard", json.RootElement.GetProperty("type").GetString());
        return template;
    }
}
