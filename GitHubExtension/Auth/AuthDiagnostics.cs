// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Api;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal static class AuthDiagnostics
{
    internal static async Task<T> RunAsync<T>(DiagnosticEvent stage, Func<Task<T>> action, CancellationToken cancellationToken)
    {
        using var operation = OperationDiagnostics.Begin(stage, DiagnosticArea.Auth);
        try
        {
            var result = await action().ConfigureAwait(false);
            operation.Complete();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            operation.Cancel();
            throw;
        }
        catch (Exception ex)
        {
            operation.Fail(ex, Classify(ex));
            throw;
        }
    }

    internal static void Run(DiagnosticEvent stage, Action action)
    {
        using var operation = OperationDiagnostics.Begin(stage, DiagnosticArea.Auth);
        try
        {
            action();
            operation.Complete();
        }
        catch (Exception ex)
        {
            operation.Fail(ex, Classify(ex));
            throw;
        }
    }

    private static DiagnosticFailure Classify(Exception exception)
    {
        var category = OperationDiagnostics.FailureCategory(exception);
        return category != DiagnosticFailure.Unexpected ? category
            : exception is CredentialFailureException ? DiagnosticFailure.Credentials
            : exception is GitHubAuthException { InnerException: { } inner } ? Classify(inner)
            : exception is GitHubAuthException ? DiagnosticFailure.Authentication
            : category;
    }
}
