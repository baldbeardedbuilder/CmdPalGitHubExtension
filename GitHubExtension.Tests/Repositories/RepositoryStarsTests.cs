// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Repositories;

[TestClass]
public sealed class RepositoryStarsTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private static readonly Uri Next = new("https://api.github.com/user/starred?page=2&per_page=50");
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(HttpStatusCode.NoContent, true)]
    [DataRow(HttpStatusCode.NotFound, false)]
    public async Task PersonalState_UsesDedicatedEndpoint(HttpStatusCode status, bool expected)
    {
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.github.com/user/starred/o/r", request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(Account.Token, request.Headers.Authorization!.Parameter);
            return new HttpResponseMessage(status);
        }));

        Assert.AreEqual(expected, await new RepositoriesClient(http).IsStarredAsync(Account, "o/r", TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.OK)]
    public async Task PersonalState_FailureIsNotReportedAsUnstarred(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(status)));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).IsStarredAsync(Account, "o/r", TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task StarState_PreservesSsoRecovery()
    {
        using var http = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-GitHub-SSO", "required; url=https://github.com/orgs/o/sso");
            return response;
        }));
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).IsStarredAsync(Account, "o/r", TestContext.CancellationToken));
        Assert.AreEqual(new Uri("https://github.com/orgs/o/sso"), error.AuthorizeUrl);
    }

    [TestMethod]
    public async Task Mutations_UseEnterpriseHostAndEmptyPutDeleteBodies()
    {
        Assert.IsTrue(GitHubHost.TryParse("git.example.com", out var host));
        var account = Account with { Host = host! };
        var requests = new List<HttpMethod>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.Method);
            Assert.AreEqual("https://git.example.com/api/v3/user/starred/o/r", request.RequestUri!.AbsoluteUri);
            Assert.IsNull(request.Content);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var client = new RepositoriesClient(http);
        await client.SetStarredAsync(account, "o/r", true, TestContext.CancellationToken);
        await client.SetStarredAsync(account, "o/r", false, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { HttpMethod.Put, HttpMethod.Delete }, requests);
    }

    [TestMethod]
    public async Task StarredClient_FollowsPaginationAndRejectsUnrelatedRoutes()
    {
        var requests = new List<Uri>();
        using var http = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"full_name":"o/r","html_url":"https://github.com/o/r","stargazers_count":999}]"""),
            };
            if (requests.Count == 1)
            {
                response.Headers.Add("Link", $"<{Next}>; rel=\"next\"");
            }

            return response;
        }));
        var client = new RepositoriesClient(http);
        var first = await client.GetStarredAsync(Account, null, TestContext.CancellationToken);
        var second = await client.GetStarredAsync(Account, first.NextPage, TestContext.CancellationToken);
        Assert.AreEqual(Next, requests[1]);
        Assert.AreEqual("o/r", second.Repositories.Single().FullName);
        Assert.IsNull(second.NextPage);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetStarredAsync(Account, new Uri("https://api.github.com/user/repos?page=2"), TestContext.CancellationToken));
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            client.GetStarredAsync(Account, new Uri("https://evil.example/user/starred?page=2"), TestContext.CancellationToken));
        Assert.HasCount(2, requests);
    }

    [TestMethod]
    public async Task StarredView_PagesWhileFilteredAndKeepsRepositoryNavigation()
    {
        var repos = new Mock<IRepositoriesClient>(MockBehavior.Strict);
        var stars = new Mock<IRepositoryStarsClient>();
        stars.Setup(c => c.GetStarredAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult([RepoFormattingTests.Repo("o/first")], Next));
        stars.Setup(c => c.GetStarredAsync(Account, Next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RepositoriesPageResult([RepoFormattingTests.Repo("o/first"), RepoFormattingTests.Repo("o/second")], null));
        using var auth = Auth();
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var prs = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var page = new ReposPage(auth, repos.Object, browser, issues, prs, starsClient: stars.Object, starred: true);
        page.GetItems();
        await page.CurrentLoad;
        page.SearchText = "second";
        Assert.IsTrue(page.HasMoreItems);
        Assert.IsTrue(page.GetItems().Any(item => item.Title == "Load more"));
        page.LoadMore();
        await page.CurrentLoad;
        Assert.IsFalse(page.HasMoreItems);
        Assert.AreEqual("second", page.SearchText);
        var item = Assert.IsInstanceOfType<RepoItem>(page.GetItems().Single());
        var repository = Assert.IsInstanceOfType<RepositoryPage>(item.Command);
        Assert.AreEqual("o/second", repository.Title);
        Assert.IsTrue(repository.GetItems().Any(section => section.Title == "Manage star"));
        page.SearchText = string.Empty;
        Assert.HasCount(2, page.GetItems());
        auth.SignOut();
        Assert.IsNull(item.Command);
        Assert.IsEmpty(repository.GetItems());
        Assert.IsEmpty(page.GetItems().OfType<RepoItem>());
        repos.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Toggle_RefreshesAuthoritativeStateAndRejectsRace()
    {
        var stars = new Mock<IRepositoryStarsClient>();
        var state = false;
        stars.Setup(c => c.IsStarredAsync(Account, "o/r", It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
        stars.Setup(c => c.SetStarredAsync(Account, "o/r", true, It.IsAny<CancellationToken>()))
            .Callback(() => state = true).Returns(Task.CompletedTask);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = StarPage(stars.Object, executor);
        await page.RefreshAsync();
        await page.SetAsync(false, TestContext.CancellationToken);
        Assert.AreEqual("Starred", page.GetItems()[0].Title);
        await page.SetAsync(false, TestContext.CancellationToken);
        stars.Verify(c => c.SetStarredAsync(Account, "o/r", true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("changed", page.GetItems()[0].Subtitle);
    }

    [TestMethod]
    public async Task Toggle_UnknownWriteReconcilesWithoutResubmitting()
    {
        var stars = new Mock<IRepositoryStarsClient>();
        var state = false;
        stars.Setup(c => c.IsStarredAsync(Account, "o/r", It.IsAny<CancellationToken>())).ReturnsAsync(() => state);
        stars.Setup(c => c.SetStarredAsync(Account, "o/r", true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Unknown outcome", outcomeUnknown: true));
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = StarPage(stars.Object, executor);
        await page.RefreshAsync();
        await page.SetAsync(false, TestContext.CancellationToken);
        state = true;
        await page.SetAsync(false, TestContext.CancellationToken);
        stars.Verify(c => c.SetStarredAsync(Account, "o/r", true, It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual("Starred", page.GetItems()[0].Title);
    }

    [TestMethod]
    public async Task Toggle_AccountChangeBeforeValidationPreventsWrite()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stars = new Mock<IRepositoryStarsClient>();
        stars.Setup(c => c.IsStarredAsync(Account, "o/r", It.IsAny<CancellationToken>()))
            .Returns(async (GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return false;
            });
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = StarPage(stars.Object, executor);
        var toggle = page.SetAsync(false, TestContext.CancellationToken);
        await started.Task.WaitAsync(TestContext.CancellationToken);
        auth.SignOut();
        await toggle;
        stars.Verify(c => c.SetStarredAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsEmpty(page.GetItems());
    }

    [TestMethod]
    public async Task Confirmation_CancelIsFinalAndDoesNotMutate()
    {
        var stars = new Mock<IRepositoryStarsClient>(MockBehavior.Strict);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = StarPage(stars.Object, executor);
        var confirmation = page.Confirmation(false);
        var form = Assert.IsInstanceOfType<FormContent>(confirmation.GetContent().Single());
        form.SubmitForm("", """{"action":"cancel"}""");
        form.SubmitForm("", """{"action":"confirm"}""");
        await confirmation.CurrentSubmission;
        stars.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Unstar_RefreshesStarredViewWithoutClearingSearch()
    {
        var repos = new Mock<IRepositoriesClient>(MockBehavior.Strict);
        var stars = new Mock<IRepositoryStarsClient>();
        var starred = true;
        stars.Setup(c => c.GetStarredAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RepositoriesPageResult(starred ? [RepoFormattingTests.Repo("o/r")] : [], null));
        stars.Setup(c => c.IsStarredAsync(Account, "o/r", It.IsAny<CancellationToken>())).ReturnsAsync(() => starred);
        stars.Setup(c => c.SetStarredAsync(Account, "o/r", false, It.IsAny<CancellationToken>()))
            .Callback(() => starred = false).Returns(Task.CompletedTask);
        using var auth = Auth();
        var browser = new FakeBrowser(_ => null);
        using var issues = new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser);
        using var prs = new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser);
        using var page = new ReposPage(auth, repos.Object, browser, issues, prs, starsClient: stars.Object, starred: true);
        page.GetItems();
        await page.CurrentLoad;
        page.SearchText = "o/r";
        var item = Assert.IsInstanceOfType<RepoItem>(page.GetItems().Single());
        var starPage = Assert.IsInstanceOfType<RepositoryStarPage>(item.MoreCommands.OfType<CommandContextItem>()
            .Single(command => command.Command is RepositoryStarPage).Command);
        await starPage.RefreshAsync();
        await starPage.SetAsync(true, TestContext.CancellationToken);
        Assert.AreEqual("o/r", page.SearchText);
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Not starred", starPage.GetItems()[0].Title);
        stars.Verify(c => c.GetStarredAsync(Account, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
        repos.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task VerificationFailureAfterWrite_ReconcilesInsteadOfWritingAgain()
    {
        var stars = new Mock<IRepositoryStarsClient>();
        stars.SetupSequence(c => c.IsStarredAsync(Account, "o/r", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ThrowsAsync(new GitHubApiException("Forbidden during verification"))
            .ReturnsAsync(true);
        stars.Setup(c => c.SetStarredAsync(Account, "o/r", true, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var auth = Auth();
        using var executor = new MutationExecutor(auth);
        using var page = StarPage(stars.Object, executor);
        await page.SetAsync(false, TestContext.CancellationToken);
        Assert.AreEqual("Star state unknown", page.GetItems()[0].Title);
        await page.SetAsync(false, TestContext.CancellationToken);
        Assert.AreEqual("Starred", page.GetItems()[0].Title);
        stars.Verify(c => c.SetStarredAsync(Account, "o/r", true, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AuthService Auth() => new(new InMemoryAccountStore(Account), Mock.Of<IGitHubAuthClient>(),
        new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private static RepositoryStarPage StarPage(IRepositoryStarsClient client, MutationExecutor executor) =>
        new(client, executor, Account, "o/r", () => true, new FakeBrowser(_ => null));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
