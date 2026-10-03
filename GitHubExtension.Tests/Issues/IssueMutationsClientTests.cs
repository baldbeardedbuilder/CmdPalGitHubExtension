// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using static BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues.IssueMutationTestData;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

[TestClass]
public sealed class IssueMutationsClientTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [DataRow("Closed", "closed", "completed")]
    [DataRow("NotPlanned", "closed", "not_planned")]
    [DataRow("Open", "open", "reopened")]
    public async Task ChangeState_SendsExactStateAndReason(string stateName, string expectedState, string reason)
    {
        var state = Enum.Parse<SubjectState>(stateName);
        using var handler = new IssueTestHandler((request, _, _) =>
        {
            Assert.AreEqual("Bearer " + Account.Token, request.Headers.Authorization!.ToString());
            Assert.AreEqual("application/vnd.github+json", request.Headers.Accept.Single().ToString());
            Assert.AreEqual("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
            return Task.FromResult(Response(Json(Issue(state))));
        });
        using var http = new HttpClient(handler);
        var result = await new IssuesClient(http).ChangeStateAsync(Account, "octo/tool", 42, state, TestContext.CancellationToken);

        var request = Assert.ContainsSingle(handler.Requests);
        Assert.AreEqual(HttpMethod.Patch, request.Method);
        Assert.AreEqual(ApiUrl, request.Uri);
        using var json = JsonDocument.Parse(request.Body);
        Assert.AreEqual(expectedState, json.RootElement.GetProperty("state").GetString());
        Assert.AreEqual(reason, json.RootElement.GetProperty("state_reason").GetString());
        Assert.AreEqual(state, result.State);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangeAssignee_UsesAdditiveOrRemovalEndpoint(bool add)
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(Json(Issue()))));
        using var http = new HttpClient(handler);
        await new IssuesClient(http).ChangeAssigneeAsync(Account, "octo/tool", 42, "octocat", add, TestContext.CancellationToken);

        var request = Assert.ContainsSingle(handler.Requests);
        Assert.AreEqual(add ? HttpMethod.Post : HttpMethod.Delete, request.Method);
        Assert.AreEqual(new Uri(ApiUrl.AbsoluteUri + "/assignees"), request.Uri);
        Assert.AreEqual("""{"assignees":["octocat"]}""", request.Body);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangeLabel_EncodesNameAndDoesNotReplaceLabels(bool add)
    {
        const string name = "help wanted/\"?+#% 雪";
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("""[{"name":"bug"}]""")));
        using var http = new HttpClient(handler);
        await new IssuesClient(http).ChangeLabelAsync(Account, "octo/tool", 42, name, add, TestContext.CancellationToken);

        var request = Assert.ContainsSingle(handler.Requests);
        Assert.AreEqual(add ? HttpMethod.Post : HttpMethod.Delete, request.Method);
        Assert.AreEqual(ApiUrl.AbsoluteUri + "/labels" + (add ? string.Empty : "/" + Uri.EscapeDataString(name)), request.Uri.AbsoluteUri);
        if (add)
        {
            using var json = JsonDocument.Parse(request.Body);
            Assert.AreEqual(name, json.RootElement.GetProperty("labels")[0].GetString());
        }
        else
        {
            Assert.AreEqual(string.Empty, request.Body);
        }
    }

    [TestMethod]
    [DataRow("labels", "name")]
    [DataRow("assignees", "login")]
    public async Task Picker_FollowsPaginationOnEnterpriseHost(string resource, string field)
    {
        Assert.IsTrue(GitHubHost.TryParse("https://github.example.com", out var host));
        var account = new GitHubAccount(host, "octocat", "test-token");
        var next = new Uri(host.ApiUrl, $"repos/octo/tool/{resource}?per_page=100&page=2");
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response($$"""[{"{{field}}":"first"}]""", next: next)));
        using var http = new HttpClient(handler);
        var client = new IssuesClient(http);
        var first = resource == "labels"
            ? await client.GetLabelsAsync(account, "octo/tool", null, TestContext.CancellationToken)
            : await client.GetAssigneesAsync(account, "octo/tool", null, TestContext.CancellationToken);
        Assert.AreEqual(new Uri(host.ApiUrl, $"repos/octo/tool/{resource}?per_page=100"), handler.Requests[0].Uri);
        Assert.AreEqual(next, first.NextPage);
        if (resource == "labels")
        {
            await client.GetLabelsAsync(account, "octo/tool", next, TestContext.CancellationToken);
        }
        else
        {
            await client.GetAssigneesAsync(account, "octo/tool", next, TestContext.CancellationToken);
        }

        Assert.AreEqual(next, handler.Requests[1].Uri);
    }

    [TestMethod]
    [DataRow("https://evil.example/repos/octo/tool/labels?page=2")]
    [DataRow("https://api.github.com/repos/other/tool/labels?page=2")]
    [DataRow("https://api.github.com/repos/octo/tool/assignees?page=2")]
    public async Task Picker_RejectsUnexpectedPageBeforeSendingToken(string url)
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("[]")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).GetLabelsAsync(Account, "octo/tool", new Uri(url), TestContext.CancellationToken));
        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden)]
    [DataRow(HttpStatusCode.Unauthorized)]
    [DataRow(HttpStatusCode.UnprocessableEntity)]
    public async Task Write_DeniedOrInvalidSelectionIsNotSuccess(HttpStatusCode status)
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("{}", status)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).ChangeAssigneeAsync(Account, "octo/tool", 42, "missing", true, TestContext.CancellationToken));
        Assert.IsFalse(error.OutcomeUnknown);
        Assert.Contains(status == HttpStatusCode.UnprocessableEntity ? "422" : "GitHub", error.Message);
    }

    [TestMethod]
    [DataRow("not JSON")]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("""{"number":42,"state":"closed","html_url":"https://github.com/octo/tool/issues/42"}""")]
    public async Task Write_MalformedSuccessfulResponseIsUnknown(string body)
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, TestContext.CancellationToken));
        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Read_RejectsMissingMetadataOrPullRequest(bool pullRequest)
    {
        var body = pullRequest ? Json(Issue()).Replace("\"comments\":3", "\"pull_request\":{},\"comments\":3", StringComparison.Ordinal)
            : Json(Issue()).Replace("\"assignees\":[{\"login\":\"mona\"}]", "\"assignees\":null", StringComparison.Ordinal);
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).GetMutationIssueAsync(Account, "octo/tool", 42, TestContext.CancellationToken));
        Assert.IsFalse(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task Write_PreservesSsoAuthorizationLink()
    {
        var authorize = new Uri("https://github.com/orgs/octo/sso");
        using var handler = new IssueTestHandler((_, _, _) =>
        {
            var response = Response("{}", HttpStatusCode.Forbidden);
            response.Headers.Add("X-GitHub-SSO", $"required; url={authorize}");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).ChangeLabelAsync(Account, "octo/tool", 42, "bug", false, TestContext.CancellationToken));
        Assert.AreEqual(authorize, error.AuthorizeUrl);
        Assert.IsFalse(error.OutcomeUnknown);
    }

    [TestMethod]
    [DataRow(".")]
    [DataRow("..")]
    public async Task RemoveDotLabel_PreservesEncodedPathSegment(string label)
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("[]")));
        using var http = new HttpClient(handler);
        await new IssuesClient(http).ChangeLabelAsync(Account, "octo/tool", 42, label, false, TestContext.CancellationToken);
        var request = Assert.ContainsSingle(handler.Requests);
        Assert.AreEqual(ApiUrl.AbsoluteUri + "/labels/" + (label == "." ? "%2E" : "%2E%2E"), request.Uri.AbsoluteUri);
        Assert.AreEqual(HttpMethod.Delete, request.Method);
    }

    [TestMethod]
    public async Task MutationDiagnostics_ContainOnlyTypedOperationData()
    {
        var entries = new List<DiagnosticEntry>();
        using var sink = OperationDiagnostics.UseSink(entries.Add, verboseReads: true);
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response("""[{"name":"private label"}]""")));
        using var http = new HttpClient(handler);
        await new IssuesClient(http).ChangeLabelAsync(Account, "private-owner/private-repo", 42, "private label", true, TestContext.CancellationToken);

        Assert.IsTrue(entries.Any(entry => entry.Event == DiagnosticEvent.RestRequest && entry.Method == "POST" && entry.Area == DiagnosticArea.Issues));
        var text = string.Join("\n", entries);
        Assert.DoesNotContain("private-owner", text);
        Assert.DoesNotContain("private-repo", text);
        Assert.DoesNotContain("private label", text);
        Assert.DoesNotContain(Account.Token, text);
        Assert.DoesNotContain(Account.Host.ApiUrl.AbsoluteUri, text);
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("""["bug"]""")]
    [DataRow("""[{"name":null}]""")]
    [DataRow("""[{"name":""}]""")]
    public async Task LabelWrite_InvalidResponseCannotConfirmRemoval(string body)
    {
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).ChangeLabelAsync(Account, "octo/tool", 42, "bug", false, TestContext.CancellationToken));
        Assert.IsTrue(error.OutcomeUnknown);
    }

    [TestMethod]
    public async Task CloseCompleted_RequiresAnExplicitCloseReasonInResponse()
    {
        var body = Json(Issue(SubjectState.Closed)).Replace("\"state_reason\":\"completed\"", "\"state_reason\":null", StringComparison.Ordinal);
        using var handler = new IssueTestHandler((_, _, _) => Task.FromResult(Response(body)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() =>
            new IssuesClient(http).ChangeStateAsync(Account, "octo/tool", 42, SubjectState.Closed, TestContext.CancellationToken));
        Assert.IsTrue(error.OutcomeUnknown);
    }
}
