// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Issues;

internal static class IssueMutationTestData
{
    internal static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "test-token");
    internal static readonly Uri ApiUrl = new("https://api.github.com/repos/octo/tool/issues/42");

    internal static GitHubIssue Issue(SubjectState state = SubjectState.Open, string[]? assignees = null, string[]? labels = null) =>
        new(42, "Keyboard navigation", "Description", state, new Uri("https://github.com/octo/tool/issues/42"),
            DateTimeOffset.Parse("2025-06-01T11:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "octocat", assignees ?? ["mona"], labels ?? ["bug"], 3);

    internal static string Json(GitHubIssue issue) => $$"""
        {"number":{{issue.Number}},"title":{{GitHubJson.String(issue.Title)}},"body":"Description",
         "state":"{{(issue.State == SubjectState.Open ? "open" : "closed")}}",
         "state_reason":"{{(issue.State == SubjectState.NotPlanned ? "not_planned" : issue.State == SubjectState.Open ? "reopened" : "completed")}}",
         "html_url":{{GitHubJson.String(issue.WebUrl.AbsoluteUri)}},"created_at":"2025-06-01T11:00:00Z","user":{"login":"octocat"},
         "assignees":[{{string.Join(",", issue.Assignees.Select(name => $$"""{"login":{{GitHubJson.String(name)}}}"""))}}],
         "labels":[{{string.Join(",", issue.Labels.Select(name => $$"""{"name":{{GitHubJson.String(name)}}}"""))}}],"comments":3}
        """;

    internal static AuthService CreateAuth()
    {
        var authClient = new Mock<IGitHubAuthClient>();
        authClient.Setup(c => c.GetLoginAsync(It.IsAny<GitHubHost>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Account.Login);
        return new(new InMemoryAccountStore(Account), authClient.Object, new FakeBrowser(_ => null), new OAuthOptions("id", "secret"));
    }

    internal static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK, Uri? next = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (next is not null)
        {
            response.Headers.Add("Link", $"<{next}>; rel=\"next\"");
        }

        return response;
    }
}

internal sealed class IssueTestHandler(Func<HttpRequestMessage, string, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    internal List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, request.RequestUri!, body));
        return await respond(request, body, cancellationToken);
    }
}
