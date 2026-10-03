// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using Microsoft.CommandPalette.Extensions;
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
    private static readonly string[] MoreCommands = ["Copy URL", "Copy name", "Delete Codespace", "Refresh"];
    private static readonly string[] ActiveMoreCommands = ["Close Codespace", "Copy URL", "Copy name", "Delete Codespace", "Refresh"];
    private static readonly string[] StoppedMoreCommands = ["Start Codespace", "Copy URL", "Copy name", "Delete Codespace", "Refresh"];
    private static readonly string[] StartingStates = ["Starting", "Shutdown", "Created", "Queued", "Provisioning", "Awaiting", "Updating", "Rebuilding", "Available"];

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
        CollectionAssert.AreEqual(state switch
        {
            "Available" => ActiveMoreCommands,
            "Shutdown" => StoppedMoreCommands,
            _ => MoreCommands,
        },
            item.MoreCommands.OfType<CommandContextItem>().Select(c => c.Command!.Name).ToArray());
    }

    [TestMethod]
    [DataRow("Available")]
    [DataRow("Starting")]
    [DataRow("ShuttingDown")]
    [DataRow("Unknown")]
    public async Task StartMenu_NotOfferedForActiveTransitioningOrUnknownCodespaces(string state)
    {
        using var page = CreatePage(Client([Codespace("one", state)]).Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        CollectionAssert.AreEqual(
            state == "Available" ? ActiveMoreCommands : MoreCommands,
            page.GetItems().Single().MoreCommands.OfType<CommandContextItem>().Select(c => c.Command!.Name).ToArray());
    }

    [TestMethod]
    [DataRow("Starting")]
    [DataRow("Available")]
    public async Task StartMenu_StartsSelectedCodespaceAndRefreshesUntilAvailable(string state)
    {
        var client = Client([Codespace("one"), Codespace("two", "Shutdown")], Next);
        client.Setup(c => c.StartCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one"), Codespace("two", state)], Next)))
            .ReturnsAsync(Codespace("two", state));
        using var page = CreatePage(client.Object, out var browser, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = page.GetItems().Cast<CodespaceItem>().Single(i => i.Codespace.Name == "two");
        var start = (MutationConfirmationPage)item.MoreCommands.OfType<CommandContextItem>().Single(c => c.Command is MutationConfirmationPage).Command!;

        client.Verify(c => c.StartCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains("compute", ((IFormContent)start.GetContent()[0]).TemplateJson);
        Assert.Contains("charges", ((IFormContent)start.GetContent()[0]).TemplateJson);
        ((IFormContent)start.GetContent()[0]).SubmitForm("{}", """{"action":"confirm"}""");
        await start.CurrentSubmission;

        var items = page.GetItems().OfType<CodespaceItem>().ToArray();
        Assert.HasCount(2, items);
        Assert.AreEqual("Available", items.Single(i => i.Codespace.Name == "one").Codespace.State);
        var started = items.Single(i => i.Codespace.Name == "two");
        Assert.IsFalse(started.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is MutationConfirmationPage));
        Assert.AreEqual("Available", started.Codespace.State);
        Assert.AreEqual("Active", started.Tags.Single().Text);
        Assert.IsTrue(started.MoreCommands.OfType<CommandContextItem>().Any(c => c.Command is CloseCodespaceCommand));
        Assert.IsNull(browser.LastOpened);
        Assert.IsTrue(page.HasMoreItems);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.StartCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Exactly(3));
        client.Verify(c => c.GetCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()),
            state == "Available" ? Times.Never() : Times.Once());
    }

    [TestMethod]
    public async Task StartConfirmation_CancelDoesNotStartOrLoad()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();

        var confirmation = (MutationConfirmationPage)page.StartConfirmation(item);
        ((IFormContent)confirmation.GetContent()[0]).SubmitForm("{}", """{"action":"cancel"}""");

        Assert.AreEqual("Shutdown", item.Codespace.State);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.StartCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.GetCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task StartConfirmation_AccountSwitchInvalidatesPendingConfirmation()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var confirmation = (MutationConfirmationPage)page.StartConfirmation(item);

        await auth.SignInWithTokenAsync("github.com", "new-account-token", TestContext.CancellationToken);
        ((IFormContent)confirmation.GetContent()[0]).SubmitForm("{}", """{"action":"confirm"}""");
        await confirmation.CurrentSubmission;

        client.Verify(c => c.StartCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    [DataRow("Available")]
    [DataRow("Starting")]
    [DataRow("ShuttingDown")]
    [DataRow("Failed")]
    [DataRow("Unknown")]
    public async Task Start_StateGateRejectsNonStoppedItems(string state)
    {
        var client = Client([Codespace("one", state)]);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        client.Verify(c => c.StartCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Start_PublishesTransitionalStatesAndPollsOnlySelectedCodespace()
    {
        var client = Client([Codespace("one", "Shutdown")], Next);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], Next)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.SetupSequence(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one", "Shutdown"))
            .ReturnsAsync(Codespace("one", "Created"))
            .ReturnsAsync(Codespace("one", "Queued"))
            .ReturnsAsync(Codespace("one", "Provisioning"))
            .ReturnsAsync(Codespace("one", "Awaiting"))
            .ReturnsAsync(Codespace("one", "Updating"))
            .ReturnsAsync(Codespace("one", "Rebuilding"))
            .ReturnsAsync(Codespace("one", "Available"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var states = new List<string>();
        page.ItemsChanged += (_, _) =>
        {
            // The host can synchronously read items from another thread.
            var read = Task.Run(() => ((CodespaceItem)page.GetItems()[0]).Codespace.State);
            Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(5)));
            states.Add(read.Result);
        };

        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        CollectionAssert.AreEqual(StartingStates, states);
        Assert.IsTrue(page.HasMoreItems);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Exactly(8));
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    [DataRow("Failed")]
    [DataRow("Unknown")]
    [DataRow("ShuttingDown")]
    public async Task Start_TerminalStateStopsPollingAndShowsError(string state)
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one", state));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        Assert.AreEqual(state, ((CodespaceItem)page.GetItems()[0]).Codespace.State);
        Assert.AreEqual("Couldn't start codespace", page.GetItems()[1].Title);
        Assert.Contains("Refresh", page.GetItems()[1].Subtitle);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Start_PollingIsBounded()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one", "Starting"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        Assert.Contains("still starting", page.GetItems()[1].Subtitle);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Exactly(60));
    }

    [TestMethod]
    public async Task Start_PollingRejectsChangedTargetAndDoesNotResubmit()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("different"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        Assert.AreEqual("Starting", page.GetItems().OfType<CodespaceItem>().Single().Codespace.State);
        Assert.Contains("different Codespace", page.GetItems().Last().Subtitle);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Shutdown")], null));
        await page.RefreshAsync();
        await page.StartAsync(page.GetItems().OfType<CodespaceItem>().Single());
        Assert.Contains("no duplicate request", page.GetItems().Last().Subtitle);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Start_TerminalPollingStateRemainsUncertainAfterRefresh()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one", "Failed"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Shutdown")], null));
        await page.RefreshAsync();
        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        Assert.Contains("no duplicate request", page.GetItems().Last().Subtitle);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Start_StatusAccessDeniedKeepsLastKnownState()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("GitHub said no."));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        Assert.AreEqual("Starting", ((CodespaceItem)page.GetItems()[0]).Codespace.State);
        Assert.AreEqual("GitHub said no.", page.GetItems()[1].Subtitle);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("refresh")]
    [DataRow("switch")]
    [DataRow("dispose")]
    public async Task Start_PollingCancellationDiscardsResponseAndPreventsDuplicateActivation(string cancel)
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                requested.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var start = page.StartAsync(item);
        var token = await requested.Task;
        var starting = (CodespaceItem)page.GetItems().Single();

        Assert.AreSame(start, page.StartAsync(item));
        Assert.AreSame(start, page.StartAsync(starting));
        if (cancel == "refresh")
        {
            client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("refreshed")], null));
            await page.RefreshAsync();
        }
        else if (cancel == "switch")
        {
            await auth.SignInWithTokenAsync("github.com", "new-account-token", TestContext.CancellationToken);
            var account = auth.CurrentAccount!;
            client.Setup(c => c.GetCodespacesAsync(account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("new-account")], null));
            page.GetItems();
            await page.CurrentLoad;
        }
        else
        {
            page.Dispose();
        }

        Assert.IsTrue(token.IsCancellationRequested);
        response.SetResult(Codespace("one", "Available"));
        await start;
        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("Starting", starting.Codespace.State);
        if (cancel != "dispose")
        {
            Assert.AreEqual(cancel == "refresh" ? "refreshed" : "new-account",
                ((CodespaceItem)page.GetItems().Single()).Codespace.Name);
        }

        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
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

        CollectionAssert.AreEqual(state == "Shutdown" ? StoppedMoreCommands : MoreCommands,
            page.GetItems().Single().MoreCommands.OfType<CommandContextItem>().Select(c => c.Command!.Name).ToArray());
    }

    [TestMethod]
    [DataRow("Shutdown", "Stopped")]
    [DataRow("ShuttingDown", "Stopping")]
    public async Task CloseMenu_StopsSelectedCodespaceAndUpdatesReturnedState(string state, string label)
    {
        var client = Client([Codespace("one"), Codespace("two")], Next);
        client.Setup(c => c.StopCodespaceAsync(Account, "two", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one"), Codespace("two", state)], Next)))
            .ReturnsAsync(Codespace("two", state));
        using var page = CreatePage(client.Object, out var browser, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = page.GetItems().Cast<CodespaceItem>().Single(i => i.Codespace.Name == "two");
        var close = item.MoreCommands.OfType<CommandContextItem>().Single(c => c.Command is CloseCodespaceCommand);

        ((InvokableCommand)close.Command!).Invoke();
        await page.CurrentLoad;

        var items = page.GetItems().OfType<CodespaceItem>().ToArray();
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
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [TestMethod]
    public async Task StartFailure_KeepsCodespaceAndShowsErrorThenRetryRecovers()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("github.com returned 402 Payment Required."));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();

        await page.StartAsync(item);

        var items = page.GetItems();
        Assert.AreSame(item, items[0]);
        Assert.AreEqual("Shutdown", item.Codespace.State);
        Assert.AreEqual("Couldn't start codespace", items[1].Title);
        Assert.AreEqual("github.com returned 402 Payment Required.", items[1].Subtitle);
        Assert.IsFalse(page.IsLoading);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null)))
            .ReturnsAsync(Codespace("one", "Starting"));

        await page.StartAsync(item);

        Assert.AreEqual("Available", ((CodespaceItem)page.GetItems().Single()).Codespace.State);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Exactly(2));
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
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Shutdown")], null)))
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
    public async Task StartWhilePending_DoesNotSendDuplicateRequests()
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>())).Returns(response.Task);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();

        var start = page.StartAsync(item);
        var duplicate = page.StartAsync(item);

        Assert.IsTrue(page.IsLoading);
        Assert.AreSame(start, duplicate);
        Assert.AreEqual("Shutdown", item.Codespace.State);
        response.SetResult(Codespace("one", "Starting"));
        await start;
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Start_AccountChangeOrDisposeCancelsAndDiscardsResponse(bool dispose)
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var start = page.StartAsync(item);
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
        response.SetResult(Codespace("one", "Starting"));
        await start;
        Assert.IsFalse(page.IsLoading);
        Assert.AreEqual("Shutdown", item.Codespace.State);
        if (!dispose)
        {
            Assert.IsEmpty(page.GetItems());
            Assert.AreEqual("Sign in to see your codespaces", page.EmptyContent!.Title);
        }

        await page.StartAsync(item);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Start_RefreshCancelsWaitAndDiscardsSupersededResponse()
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                started.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var start = page.StartAsync(item);
        var token = await started.Task;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Starting")], null));

        await page.RefreshAsync();
        response.SetResult(Codespace("one", "Available"));
        await start;

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.AreEqual("Starting", ((CodespaceItem)page.GetItems().Single()).Codespace.State);
        Assert.IsFalse(page.IsLoading);
        await page.StartAsync(item);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Close_RefreshCancelsWaitAndDiscardsSupersededResponse()
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
    public async Task Start_RefreshCancelsPreflightWithoutSubmitting()
    {
        var response = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one", "Shutdown")]);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri? _, CancellationToken token) =>
            {
                requested.SetResult(token);
                return response.Task;
            });

        var start = page.StartAsync((CodespaceItem)page.GetItems().Single());
        var token = await requested.Task;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Shutdown")], null));
        await page.RefreshAsync();
        await start;
        response.SetResult(new CodespacesPageResult([Codespace("one", "Shutdown")], null));

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Start_RefreshAfterSubmissionReconcilesWithoutDuplicateWrite()
    {
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                submitted.SetResult(token);
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        var start = page.StartAsync((CodespaceItem)page.GetItems().Single());
        var token = await submitted.Task;
        await page.RefreshAsync();
        await start;
        response.SetResult(Codespace("one"));
        await page.StartAsync((CodespaceItem)page.GetItems().Single());

        Assert.IsTrue(token.IsCancellationRequested);
        Assert.Contains("no duplicate request", page.GetItems().Last().Subtitle);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StartConfirmation_FreshStateCheckPreventsStaleWrite()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        var confirmation = (MutationConfirmationPage)page.StartConfirmation(item);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Available")], null));

        var form = (IFormContent)confirmation.GetContent().Single();
        form.SubmitForm("{}", """{"action":"confirm"}""");
        await confirmation.CurrentSubmission;
        Assert.Contains("target changed", form.TemplateJson);
        client.Verify(c => c.StartCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task StartConfirmation_FailureAndSsoLinkRemainVisibleOnConfirmationPage()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Authorize access.", authorizeUrl: new Uri("https://github.com/orgs/example/sso")));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var confirmation = (MutationConfirmationPage)page.StartConfirmation((CodespaceItem)page.GetItems().Single());
        var form = (IFormContent)confirmation.GetContent().Single();

        form.SubmitForm("{}", """{"action":"confirm"}""");
        form.SubmitForm("{}", """{"action":"confirm"}""");
        await confirmation.CurrentSubmission;
        Assert.Contains("Authorize access.", form.TemplateJson);
        Assert.Contains("https://github.com/orgs/example/sso", form.TemplateJson);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StartUnknown_ReconcilesBeforeRetryAndObservesEventualCompletion()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Response lost.", outcomeUnknown: true));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        await page.StartAsync(item);
        await page.StartAsync(item);
        Assert.Contains("no duplicate request", page.GetItems().Last().Subtitle);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Available")], null));
        await page.StartAsync(page.GetItems().OfType<CodespaceItem>().Single());
        Assert.AreEqual("Available", page.GetItems().OfType<CodespaceItem>().Single().Codespace.State);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Start_AuthoritativeRefreshFailurePreservesUnknownUntilReconciled()
    {
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Callback(() => client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GitHubApiException("Refresh failed.")))
            .ReturnsAsync(Codespace("one", "Available"));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var item = (CodespaceItem)page.GetItems().Single();
        await page.StartAsync(item);
        await page.StartAsync(item);
        Assert.AreEqual("Shutdown", page.GetItems().OfType<CodespaceItem>().Single().Codespace.State);
        Assert.AreEqual("Refresh failed.", page.GetItems().Last().Subtitle);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("one", "Available")], null));
        await page.StartAsync(item);
        Assert.AreEqual("Available", page.GetItems().OfType<CodespaceItem>().Single().Codespace.State);
        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
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

    [TestMethod]
    public async Task LoadFailure_SsoAuthorizationRemainsAvailableWithCoordinator()
    {
        var authorize = new Uri("https://github.com/orgs/example/sso");
        var client = Client([]);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Authorize access.", authorizeUrl: authorize));
        using var page = CreatePage(client.Object, out var browser, out _);
        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        var empty = page.EmptyContent!;
        ((InvokableCommand)empty.Command!).Invoke();
        Assert.AreEqual(authorize, browser.LastOpened);
        page.GetItems();
        Assert.AreSame(empty, page.EmptyContent);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task StartUnknown_CoordinatorReportsUnknownAndKeepsWriteGateAfterRefresh()
    {
        var entries = new System.Collections.Concurrent.ConcurrentQueue<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Enqueue);
        var client = Client([Codespace("one", "Shutdown")]);
        client.Setup(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Response lost.", outcomeUnknown: true));
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;

        await page.StartAsync((CodespaceItem)page.GetItems().Single());
        await page.RefreshAsync();
        await page.StartAsync(page.GetItems().OfType<CodespaceItem>().Single());

        client.Verify(c => c.StartCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        Assert.IsTrue(entries.Any(e => e.Event == DiagnosticEvent.CodespaceStart && e.Outcome == DiagnosticOutcome.Unknown));
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task DeleteUnknown_RefreshKeepsConfirmationBlockedUntilAuthoritativeAbsence()
    {
        var client = DeleteClient();
        client.Setup(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException("Response lost.", outcomeUnknown: true));
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        await confirmation.ConfirmAsync();
        await page.RefreshAsync();
        var item = page.GetItems().OfType<CodespaceItem>().Single();
        var repeated = (DeleteCodespacePage)item.MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is DeleteCodespacePage).Command!;

        Assert.IsFalse(repeated.GetItems().Any(i => i.Title == "Permanently delete this codespace"));
        await repeated.ConfirmAsync();
        client.Verify(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null));
        await page.RefreshAsync();
        Assert.IsEmpty(page.GetItems());
    }

    [TestMethod]
    [DataRow(false, false, "none reported", "none reported")]
    [DataRow(true, true, "WARNING", "WARNING")]
    [DataRow(null, null, "unknown", "unknown")]
    [DataRow(true, false, "WARNING", "none reported")]
    public async Task DeleteConfirmation_ShowsFreshIdentityAndNullableWarnings(
        bool? uncommitted, bool? unpushed, string expectedUncommitted, string expectedUnpushed)
    {
        var client = Client([Codespace("one")]);
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one") with { Branch = "fresh", HasUncommittedChanges = uncommitted, HasUnpushedChanges = unpushed, Ahead = 2, Behind = 3 });
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        var items = confirmation.GetItems();
        Assert.AreEqual("one", items[0].Title);
        Assert.AreEqual("microsoft/PowerToys", items[0].Subtitle);
        Assert.Contains("Push or back up work first; reported status is not a guarantee",
            items.Single(i => i.Title == "Permanent deletion").Subtitle);
        Assert.IsTrue(items.Any(i => i.Title.Contains("fresh", StringComparison.Ordinal)));
        Assert.IsTrue(items.Any(i => i.Title == "Commits ahead: 2; behind: 3"));
        Assert.IsTrue(items.Any(i => i.Title.StartsWith("Uncommitted changes:", StringComparison.Ordinal)
            && i.Title.Contains(expectedUncommitted, StringComparison.Ordinal)));
        Assert.IsTrue(items.Any(i => i.Title.StartsWith("Unpushed changes:", StringComparison.Ordinal)
            && i.Title.Contains(expectedUnpushed, StringComparison.Ordinal)));
        Assert.IsTrue(items.Any(i => i.Title == "Permanently delete this codespace"));
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeleteConfirmation_CancelNeverMutates()
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        var confirm = confirmation.GetItems().Single(i => i.Title == "Permanently delete this codespace").Command;
        confirmation.Cancel();
        ((InvokableCommand)confirm!).Invoke();
        await confirmation.CurrentOperation;
        Assert.AreEqual("one", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Delete_AcceptedButPresentThenAbsent_RefreshReconcilesWithoutRepeatingDelete()
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        await confirmation.ConfirmAsync();
        await confirmation.ConfirmAsync();
        Assert.AreEqual("one", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        Assert.IsTrue(page.GetItems().Any(i => i.Title == "Deletion pending"));
        Assert.IsTrue(confirmation.GetItems().Any(i => i.Title.Contains("Deletion pending", StringComparison.Ordinal)));
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([], null));
        await page.RefreshAsync();
        Assert.IsEmpty(page.GetItems());
        client.Verify(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Delete_ReconcilesEveryPageAndDeduplicates(bool targetOnSecondPage)
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("other")], Next));
        client.Setup(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult(
                targetOnSecondPage ? [Codespace("other"), Codespace("one")] : [Codespace("other")], null));
        await confirmation.ConfirmAsync();
        var items = page.GetItems().OfType<CodespaceItem>().ToArray();
        Assert.AreEqual(targetOnSecondPage ? 2 : 1, items.Length);
        Assert.AreEqual(targetOnSecondPage, items.Any(i => i.Codespace.Name == "one"));
        client.Verify(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("denied")]
    [DataRow("timeout")]
    public async Task Delete_FailureRetainsItemAndDoesNotRetry(string failure)
    {
        var client = DeleteClient();
        client.Setup(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException(failure));
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        await confirmation.ConfirmAsync();
        await confirmation.ConfirmAsync();
        Assert.AreEqual("one", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        Assert.IsTrue(page.GetItems().Any(i => i.Subtitle.Contains(failure, StringComparison.Ordinal)));
        Assert.IsFalse(page.IsLoading);
        client.Verify(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("failed-page")]
    [DataRow("malformed")]
    [DataRow("cycle")]
    [DataRow("total-count")]
    public async Task Delete_PartialReconciliationNeverRemovesItems(string failure)
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(failure switch
            {
                "malformed" => new CodespacesPageResult([], null, false),
                "total-count" => new CodespacesPageResult([], null, true, 1),
                _ => new CodespacesPageResult([], Next),
            });
        if (failure == "failed-page")
        {
            client.Setup(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GitHubApiException("permission denied"));
        }
        else
        {
            client.Setup(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([], Next));
        }

        await confirmation.ConfirmAsync();
        Assert.AreEqual("one", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        await page.RefreshAsync();
        Assert.AreEqual("one", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        client.Verify(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    [DataRow("denied")]
    [DataRow("timeout")]
    [DataRow("404")]
    public async Task DeleteConfirmation_DetailsFailureDoesNotOfferDelete(string error)
    {
        var client = DeleteClient();
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GitHubApiException(error));
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        Assert.IsFalse(confirmation.GetItems().Any(i => i.Title == "Permanently delete this codespace"));
        Assert.IsTrue(confirmation.GetItems().Any(i => i.Title.Contains("Git status and safety are unknown", StringComparison.Ordinal)));
        await confirmation.ConfirmAsync();
        Assert.AreEqual("one", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow("account")]
    [DataRow("refresh")]
    [DataRow("dispose")]
    public async Task DeleteConfirmation_StaleConfirmationCannotDelete(string change)
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out var auth);
        var confirmation = await DeletePageAsync(page);
        if (change == "account")
        {
            auth.SignOut();
        }
        else if (change == "refresh")
        {
            await page.RefreshAsync();
        }
        else
        {
            page.Dispose();
        }

        await confirmation.ConfirmAsync();
        Assert.IsFalse(confirmation.GetItems().Any(i => i.Title == "Permanently delete this codespace"));
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DeleteConfirmation_AccountChangesDuringDetailsOrDeleteDiscardResponse(bool deleting)
    {
        var client = DeleteClient();
        var details = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (deleting)
        {
            client.Setup(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
                .Returns(() => { started.SetResult(); return deletion.Task; });
        }
        else
        {
            client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
                .Returns(() => { started.SetResult(); return details.Task; });
        }

        using var page = CreatePage(client.Object, out _, out var auth);
        page.GetItems();
        await page.CurrentLoad;
        var item = page.GetItems().OfType<CodespaceItem>().Single();
        var confirmation = (DeleteCodespacePage)item.MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is DeleteCodespacePage).Command!;
        confirmation.GetItems();
        if (deleting)
        {
            await confirmation.CurrentOperation;
            _ = confirmation.ConfirmAsync();
        }

        await started.Task;
        var oldOperation = confirmation.CurrentOperation;
        auth.SignOut();
        await auth.SignInWithTokenAsync("github.com", "t", TestContext.CancellationToken);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("new")], null));
        page.GetItems();
        await page.CurrentLoad;
        if (deleting)
        {
            deletion.SetResult();
        }
        else
        {
            details.SetResult(Codespace("one"));
        }

        await oldOperation;
        Assert.AreEqual("new", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        await confirmation.ConfirmAsync();
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            deleting ? Times.Once() : Times.Never());
    }

    [TestMethod]
    [DataRow("refresh")]
    [DataRow("account")]
    [DataRow("dispose")]
    public async Task Delete_ConcurrentConfirmationAndLifecycleChangeNeverRepeatOrMutateStaleItems(string change)
    {
        var client = DeleteClient();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken deletionToken = default;
        client.Setup(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, string _, CancellationToken token) =>
            {
                deletionToken = token;
                started.SetResult();
                return response.Task;
            });
        using var page = CreatePage(client.Object, out _, out var auth);
        var confirmation = await DeletePageAsync(page);
        var deletion = confirmation.ConfirmAsync();
        await started.Task;
        var repeated = confirmation.ConfirmAsync();
        Assert.AreSame(deletion, repeated);
        if (change == "refresh")
        {
            client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new CodespacesPageResult([Codespace("new")], null));
            await page.RefreshAsync();
        }
        else if (change == "account")
        {
            auth.SignOut();
        }
        else
        {
            page.Dispose();
        }

        Assert.IsTrue(deletionToken.IsCancellationRequested);
        response.SetResult();
        await deletion;
        if (change == "account")
        {
            Assert.IsEmpty(page.GetItems());
        }
        else
        {
            Assert.AreEqual(change == "refresh" ? "new" : "one",
                page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        }

        client.Verify(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task Delete_AccountChangesDuringReconciliationNeverPublishesOldList()
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out var auth);
        var confirmation = await DeletePageAsync(page);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns(() => { started.SetResult(); return response.Task; });
        var deletion = confirmation.ConfirmAsync();
        await started.Task;
        auth.SignOut();
        await auth.SignInWithTokenAsync("github.com", "t", TestContext.CancellationToken);
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("new")], null));
        page.GetItems();
        await page.CurrentLoad;
        response.SetResult(new CodespacesPageResult([], null));
        await deletion;
        Assert.AreEqual("new", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        Assert.IsFalse(page.IsLoading);
    }

    [TestMethod]
    public async Task DeleteConfirmation_CancelWhileLoadingNeverResurrectsConfirmation()
    {
        var client = DeleteClient();
        var response = new TaskCompletionSource<GitHubCodespace>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>())).Returns(response.Task);
        using var page = CreatePage(client.Object, out _, out _);
        page.GetItems();
        await page.CurrentLoad;
        var confirmation = (DeleteCodespacePage)page.GetItems().Single().MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is DeleteCodespacePage).Command!;
        confirmation.GetItems();
        var loading = confirmation.CurrentOperation;
        confirmation.Cancel();
        response.SetResult(Codespace("one"));
        await loading;
        Assert.IsFalse(confirmation.GetItems().Any(i => i.Title == "Permanently delete this codespace"));
        await confirmation.ConfirmAsync();
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeleteConfirmation_ChangedRepositoryCannotBeConfirmed()
    {
        var client = DeleteClient();
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Codespace("one") with { RepositoryFullName = "other/repository" });
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        Assert.IsFalse(confirmation.GetItems().Any(i => i.Title == "Permanently delete this codespace"));
        await confirmation.ConfirmAsync();
        client.Verify(c => c.DeleteCodespaceAsync(It.IsAny<GitHubAccount>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Delete_RefreshCancelsPendingReconciliationAndDiscardsStaleResponse(bool staleResponseHasNextPage)
    {
        var client = DeleteClient();
        using var page = CreatePage(client.Object, out _, out _);
        var confirmation = await DeletePageAsync(page);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<CodespacesPageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken reconciliationToken = default;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .Returns((GitHubAccount _, Uri? _, CancellationToken token) =>
            {
                reconciliationToken = token;
                started.SetResult();
                return response.Task;
            });
        var deletion = confirmation.ConfirmAsync();
        await started.Task;
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult([Codespace("new")], null));
        await page.RefreshAsync();
        Assert.IsTrue(reconciliationToken.IsCancellationRequested);
        response.SetResult(new CodespacesPageResult([Codespace("old")], staleResponseHasNextPage ? Next : null));
        await deletion;
        Assert.AreEqual("new", page.GetItems().OfType<CodespaceItem>().Single().Codespace.Name);
        Assert.IsFalse(page.IsLoading);
        Assert.IsFalse(page.HasMoreItems);
        client.Verify(c => c.GetCodespacesAsync(Account, Next, It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<ICodespacesClient> DeleteClient()
    {
        var client = Client([Codespace("one")]);
        client.Setup(c => c.GetCodespaceAsync(Account, "one", It.IsAny<CancellationToken>())).ReturnsAsync(Codespace("one"));
        client.Setup(c => c.DeleteCodespaceAsync(Account, "one", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return client;
    }

    private static async Task<DeleteCodespacePage> DeletePageAsync(CodespacesPage page)
    {
        page.GetItems();
        await page.CurrentLoad;
        var item = page.GetItems().OfType<CodespaceItem>().Single();
        var confirmation = (DeleteCodespacePage)item.MoreCommands.OfType<CommandContextItem>()
            .Single(c => c.Command is DeleteCodespacePage).Command!;
        confirmation.GetItems();
        await confirmation.CurrentOperation;
        return confirmation;
    }

    private static GitHubCodespace Codespace(string name, string state = "Available", DateTimeOffset? lastUsed = null) =>
        new(name, $"Workspace {name}", "microsoft/PowerToys", "feature/gh-extension", state, lastUsed ?? Now, new Uri($"https://{name}.github.dev"));

    private static Mock<ICodespacesClient> Client(GitHubCodespace[] codespaces, Uri? next = null)
    {
        var client = new Mock<ICodespacesClient>();
        client.Setup(c => c.GetCodespacesAsync(Account, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodespacesPageResult(codespaces, next));
        client.Setup(c => c.GetCodespaceAsync(Account, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GitHubAccount _, string name, CancellationToken _) => Codespace(name));
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
        time.Setup(t => t.CreateTimer(It.IsAny<TimerCallback>(), It.IsAny<object?>(), It.IsAny<TimeSpan>(), It.IsAny<TimeSpan>()))
            .Returns((TimerCallback callback, object? state, TimeSpan _, TimeSpan period) =>
                TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, period));
        var createPage = createCodespacePage ? new CreateCodespacePage(auth, client, browser) : null;
        return new CodespacesPage(auth, client, browser, time.Object, createPage);
    }
}
