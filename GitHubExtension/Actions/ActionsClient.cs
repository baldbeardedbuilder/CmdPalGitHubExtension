// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Text.Json;
using BaldBeardedBuilder.CmdPal.GitHub.Api;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using static BaldBeardedBuilder.CmdPal.GitHub.Api.GitHubRest;

namespace BaldBeardedBuilder.CmdPal.GitHub.Actions;

internal interface IActionsClient
{
    Task<WorkflowRunsPageResult> GetRunsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken);
    Task<GitHubWorkflowRun> GetRunAsync(GitHubAccount account, string repository, long runId, CancellationToken cancellationToken);
    Task CancelRunAsync(GitHubAccount account, string repository, long runId, bool force, CancellationToken cancellationToken);
    Task RerunAsync(GitHubAccount account, string repository, long runId, bool failedOnly, bool debugLogging, CancellationToken cancellationToken);
    Task<WorkflowJobsPageResult> GetJobsAsync(GitHubAccount account, string repository, long runId, Uri? page, CancellationToken cancellationToken);
    Task<GitHubWorkflowJob> GetJobAsync(GitHubAccount account, string repository, long jobId, CancellationToken cancellationToken);
    Task RerunJobAsync(GitHubAccount account, string repository, long jobId, CancellationToken cancellationToken);
    Task<WorkflowArtifactsPageResult> GetArtifactsAsync(GitHubAccount account, string repository, long runId, Uri? page, CancellationToken cancellationToken);
    Task DownloadAsync(GitHubAccount account, string repository, long runId, long? artifactId, string destination, CancellationToken cancellationToken);
    Task<WorkflowPageResult> GetWorkflowsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken);
    Task<WorkflowDispatchContext> GetDispatchContextAsync(GitHubAccount account, string repository, CancellationToken cancellationToken);
    Task<WorkflowDispatchDefinition> GetDispatchDefinitionAsync(GitHubAccount account, string repository, string path, string @ref, CancellationToken cancellationToken);
    Task DispatchAsync(GitHubAccount account, string repository, long workflowId, string @ref, Dictionary<string, string> inputs, CancellationToken cancellationToken);
    Task<bool> CanCancelAsync(GitHubAccount account, string repository, CancellationToken cancellationToken);
}

internal sealed partial class ActionsClient(HttpClient httpClient, Func<HttpMessageHandler>? downloadHandlerFactory = null) : IActionsClient, IWorkflowCancellationPermissionsClient
{
    private const string TimeoutMessage = "GitHub took too long to return workflow runs. Try refreshing.";
    private readonly Func<HttpMessageHandler> _downloadHandlerFactory = downloadHandlerFactory
        ?? (() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

    public Task<WorkflowRunsPageResult> GetRunsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
    {
        var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        var uri = page ?? new Uri(account.Host.ApiUrl, $"repos/{path}/actions/runs?per_page=50");
        try
        {
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken, timeoutMessage: TimeoutMessage).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new WorkflowRunsPageResult(ParseRuns(json.RootElement), NextPage(response));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GitHubApiException(TimeoutMessage, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GitHubApiException("The connection closed while loading workflow runs. Try refreshing.", ex);
        }
    }, cancellationToken: cancellationToken);

    public async Task<GitHubWorkflowRun> GetRunAsync(GitHubAccount account, string repository, long runId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, RunUri(account, repository, runId), cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var run = ParseRun(json.RootElement);
        if (run.Id != runId)
        {
            throw new GitHubApiException("GitHub returned a different workflow run. Refresh and try again.");
        }

        return run;
    }

    public async Task CancelRunAsync(GitHubAccount account, string repository, long runId, bool force, CancellationToken cancellationToken)
    {
        var endpoint = force ? "force-cancel" : "cancel";
        var sent = false;
        await DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            sent = true;
            using var response = await SendAsync(httpClient, account, HttpMethod.Post,
                new Uri(RunUri(account, repository, runId).AbsoluteUri + $"/{endpoint}"), cancellationToken,
                timeoutMessage: "The cancellation request timed out. It may have been accepted. Refresh the run before trying again.")
                .ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Accepted)
            {
                throw new GitHubApiException("GitHub didn't confirm the cancellation request. Refresh the run before trying again.",
                    outcomeUnknown: true);
            }

            return true;
        }, name: DiagnosticEvent.Mutation, outcome: _ => DiagnosticOutcome.Accepted, mutationSent: () => sent,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RerunAsync(GitHubAccount account, string repository, long runId, bool failedOnly, bool debugLogging, CancellationToken cancellationToken)
    {
        var uri = new Uri(RunUri(account, repository, runId).AbsoluteUri + (failedOnly ? "/rerun-failed-jobs" : "/rerun"));
        using var content = new StringContent(
            debugLogging ? """{"enable_debug_logging":true}""" : """{"enable_debug_logging":false}""",
            Encoding.UTF8, "application/json");
        using var response = await SendAsync(httpClient, account, HttpMethod.Post, uri, cancellationToken, content: content,
            timeoutMessage: "The rerun request timed out. It may have been accepted. Refresh the run before trying again.").ConfigureAwait(false);
    }

    public Task<WorkflowPageResult> GetWorkflowsAsync(GitHubAccount account, string repository, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            var uri = page ?? new Uri(account.Host.ApiUrl, $"repos/{RepositoryPath(repository)}/actions/workflows?per_page=50");
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new WorkflowPageResult(ParseWorkflows(json.RootElement), NextPage(response));
        }, cancellationToken: cancellationToken);

    public Task<WorkflowDispatchContext> GetDispatchContextAsync(
        GitHubAccount account, string repository, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            var repoUri = new Uri(account.Host.ApiUrl, $"repos/{RepositoryPath(repository)}");
            using var repositoryResponse = await SendAsync(httpClient, account, HttpMethod.Get, repoUri, cancellationToken).ConfigureAwait(false);
            using var repositoryJson = await ReadJsonAsync(repositoryResponse, cancellationToken).ConfigureAwait(false);
            var defaultRef = GetString(repositoryJson.RootElement, "default_branch");
            if (string.IsNullOrWhiteSpace(defaultRef))
            {
                throw new GitHubApiException("GitHub didn't return a default branch for this repository.");
            }

            var refs = new List<string>();
            var visited = new HashSet<Uri>();
            Uri? page = null;
            do
            {
                var uri = page ?? new Uri(account.Host.ApiUrl, $"repos/{RepositoryPath(repository)}/branches?per_page=100");
                if (!visited.Add(uri))
                {
                    throw new GitHubApiException("GitHub returned a repeated branch list page.");
                }

                using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
                using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                {
                    throw new GitHubApiException("GitHub sent back branches we couldn't read.");
                }

                foreach (var entry in json.RootElement.EnumerateArray())
                {
                    if (GetString(entry, "name") is { Length: > 0 } name && !refs.Contains(name, StringComparer.Ordinal))
                    {
                        refs.Add(name);
                    }
                    else
                    {
                        DomainDiagnostics.InvalidEntry(DiagnosticArea.Actions);
                    }
                }

                page = NextPage(response);
                if (refs.Count > 1000)
                {
                    throw new GitHubApiException("This repository has too many branches to load here. Open GitHub to choose a ref.");
                }
            }
            while (page is not null);

            if (!refs.Contains(defaultRef, StringComparer.Ordinal))
            {
                refs.Insert(0, defaultRef);
            }

            return new WorkflowDispatchContext(refs, defaultRef);
        }, cancellationToken: cancellationToken);

    public async Task<WorkflowDispatchDefinition> GetDispatchDefinitionAsync(
        GitHubAccount account, string repository, string path, string @ref, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(@ref)
            || !path.StartsWith(".github/workflows/", StringComparison.Ordinal)
            || path.Contains('\\')
            || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
            || Path.GetExtension(path) is not (".yml" or ".yaml"))
        {
            throw new GitHubApiException("GitHub returned a workflow path that can't be safely validated.");
        }

        var workflowPath = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri(account.Host.ApiUrl,
            $"repos/{RepositoryPath(repository)}/contents/{workflowPath}?ref={Uri.EscapeDataString(@ref)}");
        using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || GetString(json.RootElement, "encoding") != "base64"
                || GetString(json.RootElement, "content") is not { } encoded)
            {
                throw new GitHubApiException("GitHub returned workflow content in a format Command Palette can't validate.");
            }

            return WorkflowDispatchDefinition.Parse(WorkflowDispatchDefinition.DecodeContent(encoded));
        });
    }

    public async Task DispatchAsync(
        GitHubAccount account, string repository, long workflowId, string @ref, Dictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        if (workflowId <= 0 || string.IsNullOrWhiteSpace(@ref))
        {
            throw new GitHubApiException("Choose a workflow and enter a branch or tag.");
        }

        var path = new Uri(account.Host.ApiUrl,
            $"repos/{RepositoryPath(repository)}/actions/workflows/{workflowId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/dispatches");
        var request = new WorkflowDispatchRequest(@ref.Trim(), inputs);
        using var body = new StringContent(
            JsonSerializer.Serialize(request, ActionsJsonContext.Default.WorkflowDispatchRequest),
            Encoding.UTF8, "application/json");
        var sent = false;
        await DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post, path, cancellationToken, content: body,
                timeoutMessage: "The dispatch request timed out. It may have been accepted. Refresh Actions before dispatching again.").ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.NoContent)
            {
                throw new GitHubApiException("GitHub received the dispatch request but returned an unexpected response. Refresh Actions before retrying.",
                    outcomeUnknown: true);
            }

            return true;
        }, name: DiagnosticEvent.Mutation, outcome: _ => DiagnosticOutcome.Accepted, mutationSent: () => sent,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowJobsPageResult> GetJobsAsync(
        GitHubAccount account, string repository, long runId, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            var uri = page ?? new Uri(account.Host.ApiUrl,
                $"repos/{RepositoryPath(repository)}/actions/runs/{runId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/jobs?per_page=100");
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new WorkflowJobsPageResult(ParseJobs(json.RootElement), NextPage(response));
        }, cancellationToken: cancellationToken);

    public async Task<GitHubWorkflowJob> GetJobAsync(GitHubAccount account, string repository, long jobId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(httpClient, account, HttpMethod.Get,
            new Uri(account.Host.ApiUrl, $"repos/{RepositoryPath(repository)}/actions/jobs/{jobId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
            cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
        {
            var job = ParseJob(json.RootElement);
            if (job is null || job.Id != jobId)
            {
                throw new GitHubApiException("GitHub sent back a workflow job we couldn't read.");
            }

            return job;
        });
    }

    public async Task RerunJobAsync(GitHubAccount account, string repository, long jobId, CancellationToken cancellationToken)
    {
        var sent = false;
        await DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent = true;
            using var response = await SendMutationAsync(httpClient, account, HttpMethod.Post,
                new Uri(account.Host.ApiUrl,
                    $"repos/{RepositoryPath(repository)}/actions/jobs/{jobId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/rerun"),
                cancellationToken, timeoutMessage: "The job rerun request timed out. It may have been accepted. Refresh the run before retrying.").ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Created)
            {
                throw new GitHubApiException("GitHub received the rerun request but returned an unexpected response. Refresh the run before retrying.",
                    outcomeUnknown: true);
            }

            return response.StatusCode;
        }, name: DiagnosticEvent.Mutation, outcome: _ => DiagnosticOutcome.Accepted, mutationSent: () => sent,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task<WorkflowArtifactsPageResult> GetArtifactsAsync(
        GitHubAccount account, string repository, long runId, Uri? page, CancellationToken cancellationToken) =>
        DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            var uri = page ?? new Uri(account.Host.ApiUrl,
                $"repos/{RepositoryPath(repository)}/actions/runs/{runId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/artifacts?per_page=50");
            using var response = await SendAsync(httpClient, account, HttpMethod.Get, uri, cancellationToken).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return new WorkflowArtifactsPageResult(ParseArtifacts(json.RootElement), NextPage(response));
        }, cancellationToken: cancellationToken);

    public async Task DownloadAsync(
        GitHubAccount account, string repository, long runId, long? artifactId, string destination, CancellationToken cancellationToken)
    {
        await DomainDiagnostics.RunAsync(DiagnosticArea.Actions, async () =>
        {
            try
            {
                await DownloadCoreAsync(account, repository, runId, artifactId, destination, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new GitHubApiException("The download took too long. Request it again and retry.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new GitHubApiException("The download connection closed before the file was saved. Request it again and retry.", ex);
            }
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadCoreAsync(
        GitHubAccount account, string repository, long runId, long? artifactId, string destination, CancellationToken cancellationToken)
    {
        if (runId <= 0 || artifactId is <= 0)
        {
            throw new GitHubApiException("GitHub returned an invalid workflow download identifier.");
        }

        var endpoint = artifactId is { } id
            ? new Uri(account.Host.ApiUrl, $"repos/{RepositoryPath(repository)}/actions/artifacts/{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}/zip")
            : new Uri(RunUri(account, repository, runId).AbsoluteUri + "/logs");
        using var apiClient = CreateDownloadClient();
        using var response = await SendAsync(apiClient, account, HttpMethod.Get, endpoint, cancellationToken,
            throwOnError: false, timeoutMessage: "GitHub took too long to prepare this download. Try requesting it again.").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && !IsRedirect(response.StatusCode))
        {
            throw DownloadApiError(account, response);
        }

        var location = response.Headers.Location;
        if (IsRedirect(response.StatusCode))
        {
            if (location is null)
            {
                throw new GitHubApiException("GitHub returned a download redirect without a location.");
            }

            using var downloadClient = CreateDownloadClient();
            var current = SafeHttpsUri(endpoint, location);
            for (var redirects = 0; ; redirects++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var assetResponse = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!IsRedirect(assetResponse.StatusCode))
                {
                    if (assetResponse.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound
                        or System.Net.HttpStatusCode.Forbidden)
                    {
                        throw ExpiredDownload();
                    }

                    if (!assetResponse.IsSuccessStatusCode)
                    {
                        throw new GitHubApiException("The download host couldn't provide this file. Request a fresh download from Actions.");
                    }

                    await SaveAsync(assetResponse.Content, destination, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (redirects >= 4 || assetResponse.Headers.Location is not { } next)
                {
                    throw new GitHubApiException("The download host returned too many redirects or an invalid redirect.");
                }

                current = SafeHttpsUri(current, next);
            }
        }

        await SaveAsync(response.Content, destination, cancellationToken).ConfigureAwait(false);
    }

    private HttpClient CreateDownloadClient()
    {
        var handler = _downloadHandlerFactory();
        if (handler is HttpClientHandler httpHandler)
        {
            httpHandler.AllowAutoRedirect = false;
            httpHandler.UseCookies = false;
            httpHandler.UseDefaultCredentials = false;
            httpHandler.Credentials = null;
        }
        else if (handler is SocketsHttpHandler socketsHandler)
        {
            socketsHandler.AllowAutoRedirect = false;
            socketsHandler.UseCookies = false;
            socketsHandler.Credentials = null;
        }

        return new HttpClient(handler, disposeHandler: true);
    }

    private static Uri RunUri(GitHubAccount account, string repository, long runId)
    {
        return new Uri(account.Host.ApiUrl,
            $"repos/{RepositoryPath(repository)}/actions/runs/{runId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    private static string RepositoryPath(string repository) =>
        string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));

    internal static List<GitHubWorkflow> ParseWorkflows(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("workflows", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException("GitHub sent back a workflow list we couldn't read.");
            }

            var workflows = new List<GitHubWorkflow>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || !id.TryGetInt64(out var workflowId)
                    || workflowId <= 0
                    || GetString(entry, "path") is not { Length: > 0 } path
                    || GetString(entry, "state") is not { } state)
                {
                    DomainDiagnostics.InvalidEntry(DiagnosticArea.Actions);
                    continue;
                }

                workflows.Add(new GitHubWorkflow(workflowId, GetString(entry, "name") ?? Path.GetFileName(path), path, state));
            }

            return workflows;
        });

    internal static List<GitHubWorkflowJob> ParseJobs(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("jobs", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException("GitHub sent back workflow jobs we couldn't read.");
            }

            var jobs = new List<GitHubWorkflowJob>();
            foreach (var entry in array.EnumerateArray())
            {
                if (ParseJob(entry) is { } job)
                {
                    jobs.Add(job);
                }
                else
                {
                    DomainDiagnostics.InvalidEntry(DiagnosticArea.Actions);
                }
            }

            return jobs;
        });

    private static GitHubWorkflowJob? ParseJob(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("id", out var id)
            || !id.TryGetInt64(out var jobId)
            || jobId <= 0
            || GetString(entry, "name") is not { } name
            || GetString(entry, "status") is not { } status)
        {
            return null;
        }

        var steps = new List<GitHubWorkflowStep>();
        if (entry.TryGetProperty("steps", out var stepArray) && stepArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in stepArray.EnumerateArray())
            {
                if (step.ValueKind == JsonValueKind.Object
                    && GetString(step, "name") is { } stepName
                    && GetString(step, "status") is { } stepStatus
                    && step.TryGetProperty("number", out var number)
                    && number.TryGetInt32(out var stepNumber))
                {
                    steps.Add(new GitHubWorkflowStep(stepName, stepStatus, GetString(step, "conclusion"), stepNumber));
                }
                else
                {
                    DomainDiagnostics.InvalidEntry(DiagnosticArea.Actions);
                }
            }
        }

        return new GitHubWorkflowJob(jobId, name, status, GetString(entry, "conclusion"), GetUri(entry, "html_url"), steps);
    }

    internal static List<GitHubArtifact> ParseArtifacts(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
        {
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("artifacts", out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                throw new GitHubApiException("GitHub sent back workflow artifacts we couldn't read.");
            }

            var artifacts = new List<GitHubArtifact>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("id", out var id)
                    && id.TryGetInt64(out var artifactId)
                    && artifactId > 0
                    && GetString(entry, "name") is { } name)
                {
                    var size = entry.TryGetProperty("size_in_bytes", out var sizeValue)
                        && sizeValue.TryGetInt64(out var sizeValueLong) && sizeValueLong >= 0 ? sizeValueLong : 0;
                    artifacts.Add(new GitHubArtifact(artifactId, name, size, GetBool(entry, "expired")));
                }
                else
                {
                    DomainDiagnostics.InvalidEntry(DiagnosticArea.Actions);
                }
            }

            return artifacts;
        });

    private static GitHubApiException DownloadApiError(GitHubAccount account, HttpResponseMessage response)
    {
        if (SsoRequired(account, response) is { } sso)
        {
            return sso;
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
        {
            return ExpiredDownload();
        }

        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
            or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.TooManyRequests)
        {
            return new GitHubApiException("GitHub said no. Your token might be missing a scope, or you hit a rate limit.");
        }

        return new GitHubApiException($"{account.Host.Name} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }

    private static GitHubApiException ExpiredDownload() =>
        new("This download has expired or is no longer available. Request a fresh download from Actions.");

    private static bool IsRedirect(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.MovedPermanently
            or System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.RedirectMethod
            or System.Net.HttpStatusCode.TemporaryRedirect
            or System.Net.HttpStatusCode.PermanentRedirect;

    private static Uri SafeHttpsUri(Uri current, Uri location)
    {
        var target = location.IsAbsoluteUri ? location : new Uri(current, location);
        if (target.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(target.UserInfo) || target.Port != 443)
        {
            throw new GitHubApiException("GitHub returned an unsafe download redirect.");
        }

        return target;
    }

    private static async Task SaveAsync(HttpContent content, string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new GitHubApiException("Enter a file path where the download should be saved.");
        }

        var fullPath = Path.GetFullPath(destination.Trim());
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new GitHubApiException("The destination folder doesn't exist. Choose an existing folder and try again.");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.download");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    internal static List<GitHubWorkflowRun> ParseRuns(JsonElement root) =>
        DomainDiagnostics.Read(DiagnosticArea.Actions, () =>
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("workflow_runs", out var runs)
            || runs.ValueKind != JsonValueKind.Array)
        {
            throw new GitHubApiException("GitHub sent back a workflow run list we couldn't read.");
        }

        return runs.EnumerateArray().Select(ParseRun).ToList();
    });

    private static GitHubWorkflowRun ParseRun(JsonElement run)
    {
        if (run.ValueKind != JsonValueKind.Object
            || !run.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var number)
            || GetUri(run, "html_url") is not { } url)
        {
            throw new GitHubApiException("GitHub sent back a workflow run we couldn't read.");
        }

        var actor = run.TryGetProperty("actor", out var user) && user.ValueKind == JsonValueKind.Object
            ? GetString(user, "login") ?? string.Empty
            : string.Empty;
        return new GitHubWorkflowRun(
            number, GetString(run, "name") ?? "Workflow",
            GetString(run, "display_title") ?? GetString(run, "name") ?? "Workflow run",
            actor, GetString(run, "status") ?? "unknown", GetString(run, "conclusion"),
            GetDate(run, "created_at"), url, GetString(run, "event"), GetString(run, "head_branch"),
            GetString(run, "head_sha"), GetOptionalInt(run, "run_number"), GetOptionalInt(run, "run_attempt"), GetDate(run, "updated_at"));
    }

    private static int? GetOptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
