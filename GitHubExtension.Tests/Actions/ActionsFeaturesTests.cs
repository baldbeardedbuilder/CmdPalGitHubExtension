// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Actions;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Actions;

[TestClass]
public class ActionsFeaturesTests
{
    private static readonly GitHubAccount Account = new(GitHubHost.GitHubDotCom, "octocat", "secret-token");
    private static readonly bool[] AuthorizationByDownloadHop = [true, false];
    private static readonly byte[] DownloadBytes = [1, 2, 3, 4];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DispatchSchema_ParsesSupportedInputsAndTheirDefaults()
    {
        var definition = WorkflowDispatchDefinition.Parse("""
            name: Test
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    description: Choose an environment
                    type: choice
                    required: true
                    options:
                      - test
                      - production
                  use_cache:
                    type: boolean
                  retries:
                    type: number
                    default: 3
                  note:
                    type: string
            """);

        Assert.HasCount(4, definition.Inputs);
        Assert.AreEqual("test", definition.Inputs[0].DefaultValue);
        Assert.IsTrue(definition.Inputs[0].Required);
        Assert.AreEqual("false", definition.Inputs[1].DefaultValue);
        Assert.AreEqual("3", definition.Inputs[2].DefaultValue);
        Assert.AreEqual(string.Empty, definition.Inputs[3].DefaultValue);
    }

    [TestMethod]
    public void DispatchSchema_ValidatesRequiredChoiceBooleanAndNumberValues()
    {
        var definition = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  environment:
                    type: choice
                    required: true
                    options:
                      - test
                      - production
                  enabled:
                    type: boolean
                  count:
                    type: number
            """);

        var values = definition.Validate(new Dictionary<string, string?>
        {
            ["environment"] = "production",
            ["enabled"] = "false",
            ["count"] = "1.5",
        });
        Assert.AreEqual("production", values["environment"]);
        Assert.AreEqual("false", values["enabled"]);
        Assert.AreEqual("1.5", values["count"]);

        Assert.ThrowsExactly<GitHubApiException>(() => definition.Validate(new Dictionary<string, string?> { ["enabled"] = "maybe" }));
        Assert.ThrowsExactly<GitHubApiException>(() => definition.Validate(new Dictionary<string, string?>
        {
            ["environment"] = "other", ["enabled"] = "true", ["count"] = "1",
        }));
        Assert.ThrowsExactly<GitHubApiException>(() => definition.Validate(new Dictionary<string, string?>
        {
            ["environment"] = "test", ["enabled"] = "true", ["count"] = "NaN",
        }));
        Assert.ThrowsExactly<GitHubApiException>(() => definition.Validate(new Dictionary<string, string?>
        {
            ["environment"] = "", ["enabled"] = "true", ["count"] = "1",
        }));
    }

    [TestMethod]
    public void DispatchSchema_PreservesWhitespaceInStringAndChoiceValues()
    {
        var definition = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  note:
                    type: string
                    default: "  keep spaces  " # retain spaces in quotes
                  mode:
                    type: choice
                    options:
                      - " release candidate " # valid choice
            """);

        var values = definition.Validate(new Dictionary<string, string?>
        {
            ["note"] = "  user value  ",
            ["mode"] = " release candidate ",
        });

        Assert.AreEqual("  user value  ", values["note"]);
        Assert.AreEqual(" release candidate ", values["mode"]);
        Assert.AreEqual("  keep spaces  ", definition.Inputs[0].DefaultValue);
    }

    [TestMethod]
    public void DispatchSchema_RejectsInputsOverGitHubPayloadLimit()
    {
        var definition = WorkflowDispatchDefinition.Parse("""
            on:
              workflow_dispatch:
                inputs:
                  payload:
                    type: string
            """);

        var error = Assert.ThrowsExactly<GitHubApiException>(() =>
            definition.Validate(new Dictionary<string, string?> { ["payload"] = new string('x', 65_535) }));

        Assert.Contains("size limit", error.Message);
    }

    [TestMethod]
    [DataRow("on:\n  push:\n    branches: [main]\n", "doesn't define workflow_dispatch")]
    [DataRow("on:\n  workflow_dispatch:\n    inputs:\n      mode:\n        type: choice\n        options: []\n", "can't validate yet")]
    [DataRow("on:\n\tworkflow_dispatch:\n", "can't validate yet")]
    [DataRow("on:\n  workflow_dispatch: { inputs: {} }\n", "can't validate yet")]
    [DataRow("on:\n  workflow_dispatch:\n    inputs:\n      mode:\n        type: string\n        unsupported: value\n", "can't validate yet")]
    public void DispatchSchema_RejectsUnsupportedOrNonDispatchWorkflows(string yaml, string message)
    {
        var error = Assert.ThrowsExactly<GitHubApiException>(() => WorkflowDispatchDefinition.Parse(yaml));
        StringAssert.Contains(error.Message, message);
    }

    [TestMethod]
    public async Task DispatchAsync_UsesWorkflowEndpointAndSerializesValidatedValues()
    {
        using var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var http = new HttpClient(handler);

        await new ActionsClient(http).DispatchAsync(Account, "owner/repo", 1234, "feature/test",
            new Dictionary<string, string> { ["mode"] = "test", ["enabled"] = "false" }, TestContext.CancellationToken);

        var request = handler.Requests.Single();
        Assert.AreEqual(new Uri("https://api.github.com/repos/owner/repo/actions/workflows/1234/dispatches"), request.Uri);
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("Bearer " + Account.Token, request.Authorization);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.AreEqual("feature/test", body.RootElement.GetProperty("ref").GetString());
        Assert.AreEqual("test", body.RootElement.GetProperty("inputs").GetProperty("mode").GetString());
        Assert.AreEqual("false", body.RootElement.GetProperty("inputs").GetProperty("enabled").GetString());
    }

    [TestMethod]
    public async Task GetDispatchDefinitionAsync_UsesTheSelectedWorkflowFileAndRef()
    {
        const string yaml = "on:\n  workflow_dispatch:\n    inputs:\n      mode:\n        type: choice\n        options:\n          - test\n";
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(yaml));
        using var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"encoding":"base64","content":"{{encoded}}"}"""),
        });
        using var http = new HttpClient(handler);

        var definition = await new ActionsClient(http).GetDispatchDefinitionAsync(
            Account, "owner/repo", ".github/workflows/deploy.yml", "feature/test", TestContext.CancellationToken);

        Assert.AreEqual("mode", definition.Inputs.Single().Name);
        Assert.AreEqual(new Uri("https://api.github.com/repos/owner/repo/contents/.github/workflows/deploy.yml?ref=feature%2Ftest"),
            handler.Requests.Single().Uri);
        Assert.AreEqual("Bearer " + Account.Token, handler.Requests.Single().Authorization);
    }

    [TestMethod]
    public async Task GetDispatchDefinitionAsync_RejectsPathsOutsideWorkflowDirectory()
    {
        using var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new ActionsClient(http).GetDispatchDefinitionAsync(
            Account, "owner/repo", "../secret.yml", "main", TestContext.CancellationToken));

        Assert.IsEmpty(handler.Requests);
    }

    [TestMethod]
    public async Task GetJobsAsync_ParsesStepsAndUsesPagination()
    {
        var next = new Uri("https://api.github.com/repos/owner/repo/actions/runs/55/jobs?per_page=100&page=2");
        using var handler = new RecordingHandler((request, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
                ? """{"jobs":[]}"""
                : """{"jobs":[{"id":1234567890123,"name":"build","status":"completed","conclusion":"failure","steps":[{"name":"restore","status":"completed","conclusion":"success","number":1},{"name":"test","status":"completed","conclusion":"failure","number":2}]}]}"""),
            Headers = { { "Link", $"<{next}>; rel=\"next\"" } },
        });
        using var http = new HttpClient(handler);

        var result = await new ActionsClient(http).GetJobsAsync(Account, "owner/repo", 55, null, TestContext.CancellationToken);

        Assert.AreEqual(new Uri("https://api.github.com/repos/owner/repo/actions/runs/55/jobs?per_page=100"), handler.Requests[0].Uri);
        Assert.AreEqual(1234567890123, result.Jobs.Single().Id);
        Assert.AreEqual("failure", result.Jobs.Single().Steps[1].Conclusion);
        Assert.AreEqual(next, result.NextPage);
        var second = await new ActionsClient(http).GetJobsAsync(Account, "owner/repo", 55, result.NextPage, TestContext.CancellationToken);
        Assert.IsEmpty(second.Jobs);
        Assert.AreEqual(2, handler.Requests.Count);
    }

    [TestMethod]
    public async Task GetJobAndRerunJob_UseRepositoryScopedEndpoints()
    {
        using var handler = new RecordingHandler((request, _) => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":77,"name":"test","status":"completed","conclusion":"failure","steps":[]}"""),
            }
            : new HttpResponseMessage(HttpStatusCode.Created));
        using var http = new HttpClient(handler);
        var client = new ActionsClient(http);

        var job = await client.GetJobAsync(Account, "owner/repo", 77, TestContext.CancellationToken);
        await client.RerunJobAsync(Account, "owner/repo", 77, TestContext.CancellationToken);

        Assert.AreEqual("failure", job.Conclusion);
        Assert.AreEqual(new Uri("https://api.github.com/repos/owner/repo/actions/jobs/77"), handler.Requests[0].Uri);
        Assert.AreEqual(new Uri("https://api.github.com/repos/owner/repo/actions/jobs/77/rerun"), handler.Requests[1].Uri);
        Assert.AreEqual(HttpMethod.Post, handler.Requests[1].Method);
    }

    [TestMethod]
    public async Task DownloadAsync_FollowsHttpsRedirectWithoutForwardingTokenAndSavesAtomically()
    {
        var requests = new List<RequestRecord>();
        using var temp = new TemporaryDirectory();
        var handler = new RecordingHandler((request, _) =>
        {
            Assert.IsFalse(request.Headers.Contains("Cookie"));
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://downloads.example.net/signed.zip");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(DownloadBytes) };
        }, requests);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("Cookie", "shared-client-cookie");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "ambient-token");
        var destination = Path.Combine(temp.Path, "logs.zip");
        var clientsCreated = 0;

        await new ActionsClient(http, () =>
        {
            clientsCreated++;
            return handler;
        }).DownloadAsync(Account, "owner/repo", 55, null, destination, TestContext.CancellationToken);

        Assert.AreEqual(2, clientsCreated);
        CollectionAssert.AreEqual(AuthorizationByDownloadHop, handler.Requests.Select(request => request.Authorization is not null).ToArray());
        Assert.AreEqual(new Uri("https://api.github.com/repos/owner/repo/actions/runs/55/logs"), handler.Requests[0].Uri);
        Assert.AreEqual(new Uri("https://downloads.example.net/signed.zip"), handler.Requests[1].Uri);
        CollectionAssert.AreEqual(DownloadBytes, await File.ReadAllBytesAsync(destination, TestContext.CancellationToken));
        Assert.IsFalse(Directory.EnumerateFiles(temp.Path, "*.download").Any());
    }

    [TestMethod]
    public async Task DownloadAsync_RejectsExpiredSignedAsset()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://downloads.example.net/signed.zip");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.Gone);
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new ActionsClient(http, () => handler).DownloadAsync(
            Account, "owner/repo", 55, 9, Path.Combine(temp.Path, "artifact.zip"), TestContext.CancellationToken));

        StringAssert.Contains(error.Message, "expired");
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "artifact.zip")));
        Assert.AreEqual("https://api.github.com/repos/owner/repo/actions/artifacts/9/zip", handler.Requests[0].Uri!.AbsoluteUri);
    }

    [TestMethod]
    public async Task DownloadAsync_RejectsUnsafeRedirectBeforeMakingSecondRequest()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new RecordingHandler((_, _) =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
            redirect.Headers.Location = new Uri("http://downloads.example.net/file.zip");
            return redirect;
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new ActionsClient(http, () => handler).DownloadAsync(
            Account, "owner/repo", 55, null, Path.Combine(temp.Path, "logs.zip"), TestContext.CancellationToken));

        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task DownloadAsync_CallerCancellationLeavesNoPartialFile()
    {
        using var temp = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var destination = Path.Combine(temp.Path, "logs.zip");
        await File.WriteAllTextAsync(destination, "existing", TestContext.CancellationToken);
        using var handler = new RecordingHandler((request, token) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://downloads.example.net/file.zip");
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CancellationAfterReadStream(cancellation)),
            };
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<OperationCanceledException>(() => new ActionsClient(http, () => handler).DownloadAsync(
            Account, "owner/repo", 55, null, destination, cancellation.Token));

        Assert.AreEqual("existing", await File.ReadAllTextAsync(destination, TestContext.CancellationToken));
        Assert.IsFalse(Directory.EnumerateFiles(temp.Path).Any(path => path.EndsWith(".download", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task DownloadAsync_MapsApiAccessErrorsWithoutFollowingRedirect()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new RecordingHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new ActionsClient(http, () => handler).DownloadAsync(
            Account, "owner/repo", 55, null, Path.Combine(temp.Path, "logs.zip"), TestContext.CancellationToken));

        StringAssert.Contains(error.Message, "token might be missing a scope");
        Assert.HasCount(1, handler.Requests);
    }

    [TestMethod]
    public async Task DownloadAsync_MapsRedirectHostNetworkErrors()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new RecordingHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://downloads.example.net/file.zip");
                return redirect;
            }

            throw new HttpRequestException("untrusted remote error detail");
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExactlyAsync<GitHubApiException>(() => new ActionsClient(http, () => handler).DownloadAsync(
            Account, "owner/repo", 55, null, Path.Combine(temp.Path, "logs.zip"), TestContext.CancellationToken));

        StringAssert.Contains(error.Message, "connection closed");
        Assert.IsFalse(error.Message.Contains("untrusted remote error detail", StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(Path.Combine(temp.Path, "logs.zip")));
    }

    private sealed record RequestRecord(Uri? Uri, HttpMethod Method, string? Authorization, string? Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond,
        List<RequestRecord>? records = null) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = records ?? [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RequestRecord(request.RequestUri, request.Method, request.Headers.Authorization?.ToString(), body));
            return respond(request, cancellationToken);
        }
    }

    private sealed class CancellationAfterReadStream(CancellationTokenSource cancellation) : MemoryStream([1, 2, 3, 4])
    {
        private bool _cancelled;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            if (read > 0 && !_cancelled)
            {
                _cancelled = true;
                cancellation.Cancel();
            }

            return read;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cmdpal-actions-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
