// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace BaldBeardedBuilder.CmdPal.GitHub.Api;

internal enum DiagnosticEvent
{
    PageLoad,
    PageSearch,
    Mutation,
    NotificationRead,
    NotificationDone,
    CodespaceStart,
    CodespaceStop,
    CodespaceCreate,
    RestRequest,
    SchemaRead,
    AuthSignIn,
    AuthTokenSignIn,
    AuthBrowser,
    AuthCallback,
    AuthExchange,
    AuthIdentity,
    AuthSignOut,
    CredentialLoad,
    CredentialSave,
    CredentialClear,
}

internal enum DiagnosticArea { None, Notifications, Repositories, Issues, PullRequests, Actions, Agents, Codespaces, Auth }
internal enum DiagnosticOutcome { Requested, Accepted, Completed, Failed, Unknown, Cancelled, Partial }
internal enum DiagnosticSeverity { Information, Warning, Error }
internal enum DiagnosticFailure { None, Http, Transport, Timeout, Schema, Credentials, Authentication, Unexpected }

internal sealed record DiagnosticEntry(
    DiagnosticEvent Event,
    DiagnosticArea Area,
    Guid OperationId,
    DiagnosticSeverity Severity,
    DiagnosticOutcome Outcome,
    long DurationMs,
    DiagnosticFailure Failure,
    int? Status,
    string? Method,
    string? Route)
{
    public override string ToString() =>
        FormattableString.Invariant($"event={Event}; area={Area}; operation-id={OperationId:N}; severity={Severity}; outcome={Outcome}; duration-ms={DurationMs}; failure={Failure}; status={Status}; method={Method}; route={Route}");
}

/// <summary>
/// Only typed categories, numbers, and route templates cross the diagnostic boundary.
/// </summary>
internal static partial class OperationDiagnostics
{
    private static readonly AsyncLocal<Operation?> Current = new();
    private static readonly AsyncLocal<Action<DiagnosticEntry>?> LocalSink = new();
    private static readonly AsyncLocal<bool?> LocalVerboseReads = new();
    private static readonly ConditionalWeakTable<Exception, FailureContext> LoggedFailures = new();

    internal static bool VerboseReads =>
        LocalVerboseReads.Value
        ?? string.Equals(Environment.GetEnvironmentVariable("CMDPAL_GITHUB_VERBOSE_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    internal static IDisposable UseSink(Action<DiagnosticEntry> sink, bool? verboseReads = null)
    {
        var previous = LocalSink.Value;
        var previousVerbose = LocalVerboseReads.Value;
        LocalSink.Value = sink;
        LocalVerboseReads.Value = verboseReads;
        return new RestoreSink(previous, previousVerbose);
    }

    internal static Operation Begin(DiagnosticEvent name, DiagnosticArea area = DiagnosticArea.None, bool verbose = false,
        Guid? operationId = null) =>
        new(name, area, verbose, operationId);

    internal static async Task<T> RunAsync<T>(DiagnosticEvent name, Func<Task<T>> action,
        DiagnosticArea area = DiagnosticArea.None, bool verbose = false, CancellationToken cancellationToken = default)
    {
        using var operation = Begin(name, area, verbose);
        try
        {
            var result = await action().ConfigureAwait(false);
            operation.Complete();
            return result;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            MarkLogged(ex, new(DiagnosticFailure.None, DiagnosticOutcome.Cancelled));
            throw;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    internal static Task RunAsync(DiagnosticEvent name, Func<Task> action,
        DiagnosticArea area = DiagnosticArea.None, bool verbose = false, CancellationToken cancellationToken = default) =>
        RunAsync(name, async () => { await action().ConfigureAwait(false); return true; }, area, verbose, cancellationToken);

    internal static T Run<T>(DiagnosticEvent name, Func<T> action)
    {
        using var operation = Begin(name);
        try
        {
            var result = action();
            operation.Complete();
            return result;
        }
        catch (Exception ex)
        {
            operation.Fail(ex);
            throw;
        }
    }

    internal static void Run(DiagnosticEvent name, Action action) =>
        Run(name, () => { action(); return true; });

    internal static DiagnosticFailure Classify(Exception exception) => exception switch
    {
        OperationCanceledException => DiagnosticFailure.Timeout,
        HttpRequestException or IOException => DiagnosticFailure.Transport,
        System.Text.Json.JsonException => DiagnosticFailure.Schema,
        _ when exception.InnerException is { } inner => Classify(inner),
        _ => DiagnosticFailure.Unexpected,
    };

    private static FailureContext? GetFailure(Exception exception) =>
        LoggedFailures.TryGetValue(exception, out var context) ? context
            : exception.InnerException is { } inner ? GetFailure(inner) : null;

    internal static DiagnosticFailure FailureCategory(Exception exception) =>
        GetFailure(exception)?.Failure ?? Classify(exception);

    internal static void CorrelateFailure(Exception source, Exception target)
    {
        if (GetFailure(source) is { } context)
        {
            MarkLogged(target, context);
        }
    }

    private static void MarkLogged(Exception exception, FailureContext context) => LoggedFailures.GetValue(exception, _ => context);

    private static void Write(DiagnosticEntry entry, Action<string>? textSink)
    {
        try
        {
            if (textSink is not null)
            {
                textSink(entry.ToString());
            }
            else if (LocalSink.Value is { } sink)
            {
                sink(entry);
            }
            else
            {
                ExtensionHost.LogMessage(new LogMessage(entry.ToString())
                {
                    State = entry.Severity == DiagnosticSeverity.Error ? MessageState.Error
                        : entry.Severity == DiagnosticSeverity.Warning ? MessageState.Warning : MessageState.Info,
                });
            }
        }
        catch
        {
            // Diagnostics must never change the result of an extension operation.
        }
    }

    internal sealed partial class Operation : IDisposable
    {
        private readonly Operation? _parent;
        private readonly DiagnosticEvent _name;
        private readonly DiagnosticArea _area;
        private readonly bool _verbose;
        private readonly long _started = Stopwatch.GetTimestamp();
        private DiagnosticOutcome _outcome = DiagnosticOutcome.Unknown;
        private DiagnosticOutcome? _mutationOutcome;
        private bool _finished;

        internal Operation(DiagnosticEvent name, DiagnosticArea area, bool verbose, Guid? operationId)
        {
            _parent = Current.Value;
            _name = name;
            _area = area == DiagnosticArea.None ? _parent?._area ?? area : area;
            _verbose = verbose;
            Id = operationId ?? _parent?.Id ?? Guid.NewGuid();
            Current.Value = this;
            if (!verbose || VerboseReads)
            {
                Emit(DiagnosticOutcome.Requested, DiagnosticSeverity.Information);
            }
        }

        internal Guid Id { get; }

        internal void Complete(DiagnosticOutcome? outcome = null, int? status = null,
            HttpMethod? method = null, Uri? uri = null, Action<string>? textSink = null)
        {
            _outcome = outcome ?? _mutationOutcome ?? DiagnosticOutcome.Completed;
            _finished = true;
            if (_parent is not null && (_name is DiagnosticEvent.Mutation or DiagnosticEvent.NotificationRead
                or DiagnosticEvent.NotificationDone or DiagnosticEvent.CodespaceStart
                or DiagnosticEvent.CodespaceStop or DiagnosticEvent.CodespaceCreate
                || _name == DiagnosticEvent.RestRequest && method is not null && method != HttpMethod.Get && method != HttpMethod.Head))
            {
                _parent._mutationOutcome = _outcome;
            }

            if (!_verbose || VerboseReads)
            {
                Emit(_outcome, DiagnosticSeverity.Information, status: status, method: method, uri: uri, textSink: textSink);
            }
        }

        internal void Cancel() => Complete(DiagnosticOutcome.Cancelled);

        internal void Fail(Exception exception, DiagnosticFailure? failure = null, int? status = null,
            HttpMethod? method = null, Uri? uri = null, Action<string>? textSink = null,
            DiagnosticOutcome? outcome = null)
        {
            var previous = GetFailure(exception);
            _outcome = outcome ?? previous?.Outcome ?? DiagnosticOutcome.Failed;
            _finished = true;
            var category = failure ?? previous?.Failure ?? Classify(exception);
            var severity = previous is not null ? DiagnosticSeverity.Information
                : _outcome == DiagnosticOutcome.Unknown ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error;
            MarkLogged(exception, new(category, _outcome));
            Emit(_outcome, severity, category, status, method, uri, textSink);
        }

        private void Emit(DiagnosticOutcome outcome, DiagnosticSeverity severity,
            DiagnosticFailure failure = DiagnosticFailure.None, int? status = null,
            HttpMethod? method = null, Uri? uri = null, Action<string>? textSink = null) =>
            Write(new DiagnosticEntry(_name, _area, Id, severity, outcome,
                (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds, failure, status,
                method is null ? null : method == HttpMethod.Get ? "GET" : method == HttpMethod.Post ? "POST"
                    : method == HttpMethod.Patch ? "PATCH" : method == HttpMethod.Delete ? "DELETE"
                    : method == HttpMethod.Put ? "PUT" : "OTHER",
                uri is null ? null : RouteTemplate(uri)), textSink);

        public void Dispose()
        {
            Current.Value = _parent;
            if (!_finished && (!_verbose || VerboseReads))
            {
                Emit(_outcome, DiagnosticSeverity.Warning);
            }
        }
    }

    internal static string RouteTemplate(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
        {
            return "unknown";
        }

        var path = uri.AbsolutePath;
        if (path.StartsWith("/api/v3/", StringComparison.Ordinal))
        {
            path = path[7..];
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts switch
        {
            ["repos", _, _] => "/repos/{owner}/{repo}",
            ["repos", _, _, "issues"] => "/repos/{owner}/{repo}/issues",
            ["repos", _, _, "issues", _] => "/repos/{owner}/{repo}/issues/{number}",
            ["repos", _, _, "pulls"] => "/repos/{owner}/{repo}/pulls",
            ["repos", _, _, "pulls", _] => "/repos/{owner}/{repo}/pulls/{number}",
            ["repos", _, _, "actions", "runs"] => "/repos/{owner}/{repo}/actions/runs",
            ["repositories", _] => "/repositories/{id}",
            ["notifications"] => "/notifications",
            ["notifications", "threads", _] => "/notifications/threads/{id}",
            ["user"] => "/user",
            ["user", "repos"] => "/user/repos",
            ["user", "codespaces"] => "/user/codespaces",
            ["user", "codespaces", _, "start"] => "/user/codespaces/{name}/start",
            ["user", "codespaces", _, "stop"] => "/user/codespaces/{name}/stop",
            ["agents", "tasks"] => "/agents/tasks",
            ["agents", "tasks", _] => "/agents/tasks/{id}",
            ["search", "repositories"] => "/search/repositories",
            ["login", "oauth", "access_token"] => "/login/oauth/access_token",
            _ => "unknown",
        };
    }

    private sealed record FailureContext(DiagnosticFailure Failure, DiagnosticOutcome Outcome);

    private sealed partial class RestoreSink(Action<DiagnosticEntry>? previous, bool? previousVerbose) : IDisposable
    {
        public void Dispose()
        {
            LocalSink.Value = previous;
            LocalVerboseReads.Value = previousVerbose;
        }
    }
}
