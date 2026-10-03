// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Api;

[TestClass]
public sealed class CollectionPayloadTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    private const string RepositoryJson = """{"full_name":"o/r","html_url":"https://github.com/o/r"}""";
    private const string NotificationJson = """
        {"id":"42","unread":false,"reason":"future_reason","updated_at":"2025-01-02T03:04:05Z",
         "subject":{"title":"Title","type":"FutureType"},
         "repository":{"full_name":"o/r","html_url":"https://github.com/o/r"}}
        """;

    [TestMethod]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("42")]
    [DataRow("\"private response content\"")]
    [DataRow("[null]")]
    [DataRow("[{}]")]
    public async Task RepositoryList_RejectsMalformedPayload(string body)
    {
        using var http = new HttpClient(new JsonHandler(() => body));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).GetMyRepositoriesAsync(Account, null, TestContext.CancellationToken));

        Assert.Contains("Try refreshing", error.Message);
        Assert.DoesNotContain("private response content", error.Message);
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("""{"items":null}""")]
    [DataRow("""{"items":{}}""")]
    [DataRow("""{"items":[null]}""")]
    [DataRow("""{"items":[],"incomplete_results":"false"}""")]
    [DataRow("""{"items":[]}""")]
    [DataRow("""{"items":[],"total_count":"0"}""")]
    [DataRow("""{"items":[],"total_count":-1}""")]
    [DataRow("""{"items":[],"total_count":0.5}""")]
    [DataRow("""{"items":[],"total_count":null}""")]
    [DataRow("""{"items":[],"total_count":2147483648}""")]
    public async Task RepositorySearch_RejectsMalformedPayload(string body)
    {
        using var http = new HttpClient(new JsonHandler(() => body));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).SearchAsync(Account, "repo", null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RepositorySearch_RejectsIncompleteResults()
    {
        using var http = new HttpClient(new JsonHandler(() =>
            $$"""{"items":[{{RepositoryJson}}],"incomplete_results":true}"""));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new RepositoriesClient(http).SearchAsync(Account, "repo", null, TestContext.CancellationToken));

        Assert.Contains("Try a more specific search", error.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Repositories_RejectMixedValidAndInvalidRows(bool search)
    {
        var items = $"[{RepositoryJson}, {{}}]";
        using var http = new HttpClient(new JsonHandler(() => search ? $$"""{"items":{{items}}}""" : items));
        var client = new RepositoriesClient(http);

        if (search)
        {
            await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
                client.SearchAsync(Account, "repo", null, TestContext.CancellationToken));
        }
        else
        {
            await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
                client.GetMyRepositoriesAsync(Account, null, TestContext.CancellationToken));
        }
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("null")]
    [DataRow("42")]
    [DataRow("[null]")]
    [DataRow("[{}]")]
    public async Task NotificationList_RejectsMalformedPayload(string body)
    {
        using var http = new HttpClient(new JsonHandler(() => body));

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetNotificationsAsync(Account, null, TestContext.CancellationToken));

        Assert.Contains("Try refreshing", error.Message);
    }

    [TestMethod]
    [DataRow("id", null)]
    [DataRow("id", "42")]
    [DataRow("id", "\" \"")]
    [DataRow("subject", "null")]
    [DataRow("subject", "[]")]
    [DataRow("subject.title", null)]
    [DataRow("subject.title", "\"\"")]
    [DataRow("subject.type", "42")]
    [DataRow("subject.url", "\"not a URL\"")]
    [DataRow("subject.url", "42")]
    [DataRow("subject.url", "\"file:///tmp/subject\"")]
    [DataRow("repository", "\"bad\"")]
    [DataRow("repository.full_name", null)]
    [DataRow("repository.html_url", "\"not a URL\"")]
    [DataRow("repository.html_url", "\"https://user@github.com/o/r\"")]
    [DataRow("reason", null)]
    [DataRow("unread", null)]
    [DataRow("unread", "\"false\"")]
    [DataRow("updated_at", null)]
    [DataRow("updated_at", "\"not a date\"")]
    public async Task Notifications_RejectInvalidFields(string path, string? value)
    {
        var row = JsonNode.Parse(NotificationJson)!.AsObject();
        var parts = path.Split('.');
        var parent = parts.Length == 1 ? row : row[parts[0]]!.AsObject();
        if (value is null)
        {
            parent.Remove(parts[^1]);
        }
        else
        {
            parent[parts[^1]] = JsonNode.Parse(value);
        }

        using var http = new HttpClient(new JsonHandler(() => $"[{row}]"));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetNotificationsAsync(Account, null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Notifications_RejectMixedValidAndInvalidRows()
    {
        using var http = new HttpClient(new JsonHandler(() => $"[{NotificationJson}, {{}}]"));

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new NotificationsClient(http).GetNotificationsAsync(Account, null, TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Collections_AcceptValidEmptyResponses()
    {
        using var http = new HttpClient(new JsonHandler(() => "[]"));
        var repositories = await new RepositoriesClient(http).GetMyRepositoriesAsync(Account, null, TestContext.CancellationToken);
        var notifications = await new NotificationsClient(http).GetNotificationsAsync(Account, null, TestContext.CancellationToken);
        using var searchHttp = new HttpClient(new JsonHandler(() => """{"items":[],"incomplete_results":false,"total_count":0}"""));
        var search = await new RepositoriesClient(searchHttp).SearchAsync(Account, "repo", null, TestContext.CancellationToken);

        Assert.IsEmpty(repositories.Repositories);
        Assert.IsNull(repositories.NextPage);
        Assert.IsEmpty(notifications.Notifications);
        Assert.IsNull(notifications.NextPage);
        Assert.IsEmpty(search.Repositories);
        Assert.AreEqual(0, search.TotalCount);
        Assert.IsNull(search.NextPage);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Notifications_AcceptOptionalUrlsAndUnknownEnumValues(bool nullUrl)
    {
        var row = JsonNode.Parse(NotificationJson)!.AsObject();
        if (nullUrl)
        {
            row["subject"]!["url"] = null;
        }

        using var http = new HttpClient(new JsonHandler(() => $"[{row}]"));
        var result = await new NotificationsClient(http).GetNotificationsAsync(Account, null, TestContext.CancellationToken);

        var notification = result.Notifications.Single();
        Assert.IsNull(notification.SubjectApiUrl);
        Assert.AreEqual("FutureType", notification.SubjectType);
        Assert.AreEqual("future_reason", notification.Reason);
        Assert.IsFalse(notification.Unread);
    }

    [TestMethod]
    public async Task RepositoriesPage_MalformedPayloadShowsErrorAndRefreshRecovers()
    {
        var body = "{}";
        using var http = new HttpClient(new JsonHandler(() => body));
        var auth = Auth();
        var browser = new FakeBrowser(_ => null);
        using var page = new ReposPage(auth, new RepositoriesClient(http), browser,
            new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser),
            new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser),
            searchDelay: TimeSpan.Zero);

        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Couldn't load your repos", page.EmptyContent!.Title);
        Assert.Contains("Try refreshing", page.EmptyContent!.Subtitle);
        Assert.IsFalse(page.IsLoading);

        body = $"[{RepositoryJson}]";
        await page.RefreshAsync();

        Assert.AreEqual("o/r", page.GetItems().Single().Title);
        Assert.AreEqual("No repos yet", page.EmptyContent!.Title);
    }

    [TestMethod]
    public async Task RepositorySearch_MalformedPayloadShowsErrorAndRefreshRecovers()
    {
        var body = "{}";
        using var http = new HttpClient(new JsonHandler(() => body));
        var auth = Auth();
        var browser = new FakeBrowser(_ => null);
        using var page = new ReposPage(auth, new RepositoriesClient(http), browser,
            new RepositoryIssuesPage(auth, Mock.Of<IIssuesClient>(), browser),
            new RepositoryPullRequestsPage(auth, Mock.Of<IPullRequestsClient>(), browser),
            searchDelay: TimeSpan.Zero);

        page.SearchText = "repo";
        await page.CurrentSearch;

        Assert.IsEmpty(page.GetItems());
        await page.CurrentLoad;
        Assert.AreEqual("Couldn't search GitHub", page.EmptyContent!.Title);
        Assert.Contains("Try refreshing", page.EmptyContent!.Subtitle);

        body = $$"""{"items":[{{RepositoryJson}}],"total_count":1}""";
        await page.RefreshAsync();
        await page.CurrentSearch;

        Assert.AreEqual("o/r", page.GetItems().Single().Title);
        Assert.AreEqual("No repos found", page.EmptyContent!.Title);
    }

    [TestMethod]
    public async Task NotificationsPage_MalformedPayloadShowsErrorAndRefreshRecovers()
    {
        var body = "{}";
        using var http = new HttpClient(new JsonHandler(() => body));
        var page = new NotificationsPage(Auth(), new NotificationsClient(http), new FakeBrowser(_ => null));

        page.GetItems();
        await page.CurrentLoad;

        Assert.IsEmpty(page.GetItems());
        Assert.AreEqual("Couldn't load notifications", page.EmptyContent!.Title);
        Assert.Contains("Try refreshing", page.EmptyContent!.Subtitle);
        Assert.IsFalse(page.IsLoading);

        body = $"[{NotificationJson}]";
        await page.RefreshAsync();

        Assert.AreEqual("Title", page.GetItems().Single().Title);
        Assert.AreEqual("You're all caught up", page.EmptyContent!.Title);
    }

    public TestContext TestContext { get; set; } = null!;

    private static AuthService Auth() => new(new InMemoryAccountStore(Account),
        Mock.Of<IGitHubAuthClient>(), new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));

    private sealed class JsonHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(), Encoding.UTF8, "application/json"),
            });
    }
}
