// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Api;

namespace BaldBeardedBuilder.CmdPal.GitHub;

internal static class DomainDiagnostics
{
    internal static T Read<T>(DiagnosticArea area, Func<T> read)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, area, verbose: true);
        try
        {
            var result = read();
            operation.Complete();
            return result;
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            operation.Fail(ex, DiagnosticFailure.Schema);
            throw;
        }
    }

    internal static void InvalidEntry(DiagnosticArea area)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.SchemaRead, area, verbose: true);
        operation.Fail(new InvalidOperationException(), DiagnosticFailure.Schema);
    }

    internal static async Task<T> RunAsync<T>(DiagnosticArea area, Func<Task<T>> action,
        DiagnosticEvent name = DiagnosticEvent.PageLoad,
        Func<T, DiagnosticOutcome>? outcome = null, Func<bool>? mutationSent = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = OperationDiagnostics.Begin(name, area,
            verbose: name is DiagnosticEvent.PageLoad or DiagnosticEvent.PageSearch);
        try
        {
            var result = await action().ConfigureAwait(false);
            operation.Complete(outcome?.Invoke(result) ?? DiagnosticOutcome.Completed);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            var failure = OperationDiagnostics.FailureCategory(ex);
            var unknown = ex is GitHubApiException { OutcomeUnknown: true }
                || (mutationSent?.Invoke() == true
                    && failure is DiagnosticFailure.Transport or DiagnosticFailure.Timeout or DiagnosticFailure.Schema);
            operation.Fail(ex, outcome: unknown ? DiagnosticOutcome.Unknown : null);
            throw;
        }
    }
}
