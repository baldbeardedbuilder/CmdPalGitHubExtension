// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal static class PageDiagnostics
{
    internal static void Finish(OperationDiagnostics.Operation operation, Exception? failure, bool current,
        DiagnosticOutcome success = DiagnosticOutcome.Completed, bool mutation = false,
        CancellationToken cancellationToken = default)
    {
        if (!current || cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
        }
        else if (failure is not null)
        {
            var ambiguous = mutation && IsAmbiguous(failure);
            operation.Fail(failure, outcome: ambiguous ? DiagnosticOutcome.Unknown : null);
        }
        else
        {
            operation.Complete(success);
        }
    }

    private static bool IsAmbiguous(Exception failure) =>
        failure is HttpRequestException or IOException or OperationCanceledException
        || failure.InnerException is { } inner && IsAmbiguous(inner);
}
