// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Codespaces;

[TestClass]
public class CodespacesPageTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "t");
    private static readonly DateTimeOffset Now = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri Next = new("https://api.github.com/user/codespaces?page=2");
    private static readonly string[] LatestThenOlder = ["latest", "older"];
    private static readonly string[] SecondThenFirst = ["second", "first"];
    private static readonly string[] MoreCommands = ["Copy URL", "Copy name", "Refresh"];
    private static readonly string[] ActiveMoreCommands = ["Close Codespace", "Copy URL", "Copy name", "Refresh"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task GetItems_ListsCodespacesWithRepositoryBranchTimeAndStatus()
    {
        var client = Client([Codespace("older", "Shutdown", Now.AddHours(-3)), Codespace("latest")]);
        using var page = CreatePage(client.Object, out _, out _);

        page.GetItems();
        await page.CurrentLoad;

        var items = page.GetItems().Cast<CodespaceItem>().ToArray();
        CollectionAssert.AreEqual(LatestThenOlder, items.Select(i => i.Codespace.Name).ToArray());
        Assert.AreEqual("microsoft/PowerToys", items[0].Title);
        Assert.AreEqual("feature/gh-extension \u00B7 just now", items[0].Subtitle);
        Assert.AreEqual("Active", items[0].Tags.Single().Text);
        Assert.AreEqual("feature/gh-extension \u00B7 3h ago", items[1].Subtitle);
        Assert.AreEqual("Stopped", items[1].Tags.Single().Text);
        Assert.AreEqual("Filter codespaces...", page.PlaceholderText);
        Assert.IsFalse(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("POWERTOYS active", "one")]
    [DataRow("feature stopped", "two")]
    [DataRow("workspace one", "one")]
    [DataRow("two", "two")]
    public async Task Search_FiltersRepositoryBranchStateAndNames(string query, string expectedName)
    {
        var client = Client([Codespace("one"), Codespace("two", "Shutdown")]);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        page.SearchText = query;

        Assert.AreEqual(expectedName, ((CodespaceItem)page.GetItems().Single()).Codespace.Name);
        page.SearchText = string.Empty;
        Assert.HasCount(2, page.GetItems());
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task LoadMore_PreservesSameRepositoryCodespacesAndRemovesDuplicateNames()
    {
        var first = Codespace("first", lastUsed: Now.AddHours(-1));
        var second = Codespace("second");
        var client = Client([first], Next);
        client.Setup(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([first, second], null));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        Assert.IsTrue(page.HasMoreItems);
        page.SearchText = "active";

        page.LoadMore();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(SecondThenFirst, page.GetItems().Cast<CodespaceItem>().Select(i => i.Codespace.Name).ToArray());
        Assert.IsFalse(page.HasMoreItems);
    }

    [TestMethod]
    [DataRow("Available")]
    [DataRow("Shutdown")]
    public async Task Open_LaunchesCodespaceInBrowser(string state)
    {
        var codespace = Codespace("one", state);
        using var page = CreatePage(Client([codespace]).Object, out var browser, out _);
        page.GetItems();
        await page.CurrentLoad;

        var item = (CodespaceItem)page.GetItems().Single();
        ((InvokableCommand)item.Command!).Invoke();

        Assert.AreEqual(codespace.WebUrl, browser.LastOpened);
        Assert.AreEqual("Open", item.Command!.Name);
        CollectionAssert.AreEqual(state == "Available" ? ActiveMoreCommands : MoreCommands,
            item.MoreCommands.OfType<CommandContextItem>().Select(c => c.Command!.Name).ToArray());
    }

    [TestMethod]
    [DataRow("Shutdown")]
    [DataRow("ShuttingDown")]
    [DataRow("Starting")]
    [DataRow("Unknown")]
    public async Task CloseMenu_NotOfferedForInactiveOrTransitioningCodespaces(string state)
    {
        using var page = CreatePage(Client([Codespace("one", state)]).Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(MoreCommands,
            page.GetItems().Single().MoreCommands.OfType<CommandContextItem>().Select(c => c.Command!.Name).ToArray());
    }

    [TestMethod]
    [DataRow("Shutdown", "Stopped")]
    [DataRow("ShuttingDown", "Stopping")]
    public async Task CloseMenu_StopsSelectedCodespaceAndUpdatesReturnedState(string state, string label)
    {
        var client = Client([Codespace("one"), Codespace("two")], Next);
        client.Setup(c => c.StopCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("two", state));
        using var page = CreatePage(client.Object, out var browser, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = page.GetItems().Cast<CodespaceItem>().Single(i => i.Codespace.Name == "two");
        var close = item.MoreCommands.OfType<CommandContextItem>().Single(c => c.Command is CloseCodespaceCommand);

        ((InvokableCommand)close.Command!).Invoke();
        await page.CurrentLoad;

        var items = page.GetItems().Cast<CodespaceItem>().ToArray();
        Assert.HasCount(2, items);
        Assert.AreEqual("Available", items.Single(i => i.Codespace.Name == "one").Codespace.State);
        var stopped = items.Single(i => i.Codespace.Name == "two");
        Assert.AreEqual(state, stopped.Codespace.State);
        Assert.AreEqual(label, stopped.Tags.Single().Text);
        Assert.IsFalse(stopped.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is CloseCodespaceCommand));
        Assert.IsNull(browser.LastOpened);
        Assert.IsTrue(page.HasMoreItems);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.StopCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CloseFailure_KeepsCodespaceAndShowsErrorThenRetryRecovers()
    {
        var client = Client([Codespace("one")]);
        client.Setup(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Your token is missing a scope"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();

        await page.CloseAsync(item);

        var items = page.GetItems();
        Assert.AreSame(item, items[0]);
        Assert.AreEqual("Available", item.Codespace.State);
        Assert.AreEqual("Couldn't close codespace", items[1].Title);
        Assert.AreEqual("Your token is missing a scope", items[1].Subtitle);
        Assert.IsFalse(page.IsLoading);
        client.Setup(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one", "Shutdown"));

        await page.CloseAsync(item);

        Assert.AreEqual("Shutdown", ((CodespaceItem)page.GetItems().Single()).Codespace.State);
        client.Verify(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [TestMethod]
    public async Task CloseWhilePending_DoesNotSendDuplicateRequests()
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one")]);
        client.Setup(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>())).Returns(response.Task);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();

        var close = page.CloseAsync(item);
        var duplicate = page.CloseAsync(item);

        Assert.IsTrue(page.IsLoading);
        Assert.AreSame(close, duplicate);
        Assert.AreEqual("Available", item.Codespace.State);
        response.SetResult(Codespace("one", "Shutdown"));
        await close;
        client.Verify(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Close_AccountChangeOrDisposeCancelsAndDiscardsResponse(bool dispose)
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one")]);
        client.Setup(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var close = page.CloseAsync(item);
        var token = await started.Task;

        if (dispose)
        {
            page.Dispose();
        }
        else
        {
            auth.SignOut();
        }

        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Codespace("one", "Shutdown"));
        await close;
        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("Available", item.Codespace.State);
        if (!dispose)
        {
            Assert.IsEmpty(page.GetItems());
            Assert.AreEqual("Sign in to see your codespaces", page.EmptyContent!.Title);
        }

        await page.CloseAsync(item);
        client.Verify(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Close_RefreshCancelsAndDiscardsSupersededResponse()
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one")]);
        client.Setup(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var close = page.CloseAsync(item);
        var token = await started.Task;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "ShuttingDown")], null));

        await page.RefreshAsync();
        response.SetResult(Codespace("one", "Shutdown"));
        await close;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual("ShuttingDown", ((CodespaceItem)page.GetItems().Single()).Codespace.State);
        Assert.IsFalse(page.IsLoading);
        await page.CloseAsync(item);
        client.Verify(c => c.StopCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CodespaceMoreMenu_OffersCreateCodespace()
    {
        var client = Client([Codespace("one")]);
        using var page = CreatePage(client.Object, out _, out _, createCodespacePage: true);
        using var createPage = page.CreatePage!;
        page.GetItems();
        await page.CurrentLoad;

        var item = (CodespaceItem)page.GetItems().Single();

        var create = item.MoreCommands.OfType<CommandContextItem>()
            .Single(context => context.Command is CreateCodespacePage);
        Assert.AreSame(createPage, create.Command);
        Assert.AreEqual("Create Codespace", create.Command!.Name);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("missing")]
    public async Task EmptyMoreMenu_OffersCreateCodespaceWithoutRows(string search)
    {
        using var page = CreatePage(Client([]).Object, out _, out _, createCodespacePage: true);
        using var createPage = page.CreatePage!;
        page.GetItems();
        await page.CurrentLoad;
        page.SearchText = search;

        Assert.IsEmpty(page.GetItems());
        var empty = page.EmptyContent!;
        var create = empty.MoreCommands.OfType<CommandContextItem>().Single();
        Assert.AreSame(createPage, create.Command);
        Assert.AreEqual("Create Codespace", create.Command!.Name);
        Assert.IsInstanceOfType<RefreshCodespacesCommand>(empty.Command);
        var commands = empty.MoreCommands;
        page.GetItems();
        Assert.AreSame(empty, page.EmptyContent);
        Assert.AreSame(commands, page.EmptyContent!.MoreCommands);
    }

    [TestMethod]
    public async Task LoadFailureMoreMenu_OffersCreateCodespaceAndRetry()
    {
        var client = Client([]);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        using var page = CreatePage(client.Object, out _, out _, createCodespacePage: true);
        using var createPage = page.CreatePage!;
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Couldn't load codespaces", page.EmptyContent!.Title);
        Assert.AreSame(createPage, page.EmptyContent.MoreCommands.OfType<CommandContextItem>().Single().Command);
        Assert.IsInstanceOfType<RefreshCodespacesCommand>(page.EmptyContent.Command);
    }

    [TestMethod]
    public async Task Refresh_ReplacesRowsAndState()
    {
        var client = Client([Codespace("one")]);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("two", "Shutdown")], null));

        var refresh = page.GetItems().Single().MoreCommands.OfType<CommandContextItem>().Single(c => c.Command is RefreshCodespacesCommand);
        ((InvokableCommand)refresh.Command!).Invoke();
        await page.CurrentLoad;

        var item = (CodespaceItem)page.GetItems().Single();
        Assert.AreEqual("two", item.Codespace.Name);
        Assert.AreEqual("Stopped", item.Tags.Single().Text);
    }

    [TestMethod]
    public async Task EmptyAndNoMatches_ShowDifferentMessages()
    {
        using var page = CreatePage(Client([]).Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No codespaces yet", page.EmptyContent!.Title);
        page.SearchText = "missing";
        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("No codespaces found", page.EmptyContent!.Title);
        Assert.AreEqual("Nothing matches \"missing\"", page.EmptyContent.Subtitle);
    }

    [TestMethod]
    public async Task LoadFailure_ShowsErrorAndRetryRecovers()
    {
        var client = Client([]);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Your token is missing a scope"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Couldn't load codespaces", page.EmptyContent!.Title);
        Assert.AreEqual("Your token is missing a scope", page.EmptyContent.Subtitle);
        Assert.IsFalse(page.IsLoading);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one")], null));

        ((InvokableCommand)page.EmptyContent.Command!).Invoke();
        await page.CurrentLoad;

        Assert.AreEqual("one", ((CodespaceItem)page.GetItems().Single()).Codespace.Name);
    }

    [TestMethod]
    public async Task PaginationFailure_KeepsRowsAndShowsError()
    {
        var client = Client([Codespace("one")], Next);
        client.Setup(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("rate limited"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        page.LoadMore();
        await page.CurrentLoad;

        var items = page.GetItems();
        Assert.AreEqual("one", ((CodespaceItem)items[0]).Codespace.Name);
        Assert.AreEqual("Couldn't load codespaces", items[1].Title);
        Assert.AreEqual("rate limited", items[1].Subtitle);
    }

    [TestMethod]
    public async Task SignOut_ClearsRowsAndPagination()
    {
        using var page = CreatePage(Client([Codespace("one")], Next).Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;

        auth.SignOut();

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Sign in to see your codespaces", page.EmptyContent!.Title);
        Assert.IsFalse(page.HasMoreItems);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task AccountChange_DiscardsOldResponseWithoutClearingNewLoadingState()
    {
        var oldResponse = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([]);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.SetResult();
                return oldResponse.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        var oldLoad = page.CurrentLoad;
        await started.Task;
        auth.SignOut();
        await auth.SignInWithTokenAsync("github.com", "t", TestContext.CancellationToken);
        var newResponse = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>())).Returns(newResponse.Task);
        page.GetItems();
        var newLoad = page.CurrentLoad;

        oldResponse.SetResult(new CodespacesPageResult([Codespace("old")], Next));
        await oldLoad;

        Assert.IsTrue(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
        Assert.IsEmpty(page.GetItems());
        newResponse.SetResult(new CodespacesPageResult([Codespace("new")], null));
        await newLoad;
        Assert.AreEqual("new", ((CodespaceItem)page.GetItems().Single()).Codespace.Name);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task Refresh_DiscardsSupersededResponse()
    {
        var oldResponse = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([]);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                started.SetResult();
                return oldResponse.Task;
            });
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        var oldLoad = page.CurrentLoad;
        await started.Task;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("new", "Shutdown")], null));

        await page.RefreshAsync();
        oldResponse.SetResult(new CodespacesPageResult([Codespace("old")], Next));
        await oldLoad;

        Assert.AreEqual("new", ((CodespaceItem)page.GetItems().Single()).Codespace.Name);
        Assert.IsFalse(page.HasMoreItems);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public void EnterpriseServer_ShowsUnsupportedMessageWithoutFetching()
    {
        Assert.IsTrue(GitHubHost.TryParse("github.example.com", out var host));
        var client = Client([]);
        using var page = CreatePage(client.Object, out _, out _, new GitHubAccount(host!, "mona", "t"));

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Codespaces isn't available here", page.EmptyContent!.Title);
        Assert.Contains("GitHub Enterprise Server", page.EmptyContent.Subtitle);
        client.Verify(c => c.GetCodespacesAsync(It.IsAny<GitHubAccount>(), It.IsAny<Uri?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static GitHubCodespace Codespace(string name, string state = "Available", DateTimeOffset? lastUsed = null) =>
        new(name, $"Workspace {name}", "microsoft/PowerToys", "feature/gh-extension", state, lastUsed ?? Now, new Uri($"https://{name}.github.dev"));

    private static Mock<ICodespacesClient> Client(GitHubCodespace[] codespaces, Uri? next = null)
    {
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult(codespaces, next));
        return client;
    }

    private static CodespacesPage CreatePage(
        ICodespacesClient client,
        out FakeBrowser browser,
        out AuthService auth,
        GitHubAccount? account = null,
        bool createCodespacePage = false)
    {
        var authClient = new Mock<IGitHubAuthClient>();
        authClient.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("octocat");
        auth = new AuthService(new InMemoryAccountStore(account ?? Account), authClient.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
        browser = new FakeBrowser(_ => null);
        var time = new Mock<TimeProvider>();
        time.Setup(t => t.GetUtcNow()).Returns(Now);
        var createPage = createCodespacePage ? new CreateCodespacePage(auth, client, browser) : null;
        return new CodespacesPage(auth, client, browser, time.Object, createPage);
    }
}
