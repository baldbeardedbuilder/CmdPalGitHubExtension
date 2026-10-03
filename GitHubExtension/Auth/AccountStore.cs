// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Windows.Security.Credentials;
using BaldBeardedBuilder.CmdPal.GitHub.Api;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal interface IAccountStore
{
    GitHubAccount? Load();

    void Save(GitHubAccount account);

    void Clear();
}

internal interface ICredentialVault
{
    IReadOnlyList<string> FindUserNames(string resource);

    string RetrievePassword(string resource, string userName);

    void Add(string resource, string userName, string password);

    void Remove(string resource, string userName);
}

internal sealed class CredentialVault : ICredentialVault
{
    public IReadOnlyList<string> FindUserNames(string resource) =>
        [.. new PasswordVault().FindAllByResource(resource).Select(credential => credential.UserName)];

    public string RetrievePassword(string resource, string userName)
    {
        var credential = new PasswordVault().Retrieve(resource, userName);
        credential.RetrievePassword();
        return credential.Password;
    }

    public void Add(string resource, string userName, string password) =>
        new PasswordVault().Add(new PasswordCredential(resource, userName, password));

    public void Remove(string resource, string userName)
    {
        var vault = new PasswordVault();
        vault.Remove(vault.Retrieve(resource, userName));
    }
}

/// <summary>
/// Keeps the signed in account in the Windows Credential Locker, so the token never touches disk in plain text.
/// </summary>
internal sealed class PasswordVaultAccountStore : IAccountStore
{
    private const string Resource = "BaldBeardedBuilder.CmdPal.GitHub";
    private const string RecoveryResource = Resource + ".Recovery";
    private const int NotFound = unchecked((int)0x80070490);
    private readonly ICredentialVault _vault;

    public PasswordVaultAccountStore(ICredentialVault? vault = null)
    {
        _vault = vault ?? new CredentialVault();
    }

    public GitHubAccount? Load()
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CredentialLoad, DiagnosticArea.Auth);
        try
        {
            var account = Load(Resource) ?? Load(RecoveryResource);
            operation.Complete();
            return account;
        }
        catch (Exception ex)
        {
            operation.Fail(ex, DiagnosticFailure.Credentials);
            if (ex is COMException or UnauthorizedAccessException or IOException)
            {
                throw new GitHubAuthException("Couldn't read the account from Windows Credential Locker. Try again.", new CredentialFailureException(ex));
            }

            throw;
        }
    }

    private GitHubAccount? Load(string resource)
    {
        foreach (var userName in FindAll(resource))
        {
            var password = _vault.RetrievePassword(resource, userName);
            var separator = userName.LastIndexOf('@');
            if (separator > 0
                && GitHubHost.TryParse(userName[(separator + 1)..], out var host)
                && !string.IsNullOrEmpty(password))
            {
                return new GitHubAccount(host, userName[..separator], password);
            }
        }

        return null;
    }

    public void Save(GitHubAccount account)
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CredentialSave, DiagnosticArea.Auth);
        try
        {
            var credentials = FindAll(Resource);
            var userName = $"{account.Login}@{account.Host.Name}";
            if (credentials.Contains(userName))
            {
                // Same-key Add is not transactional. Keep a vault-only recovery copy before replacing it.
                _vault.Add(RecoveryResource, userName, _vault.RetrievePassword(Resource, userName));
            }

            _vault.Add(Resource, userName, account.Token);
            foreach (var previous in credentials.Where(previous => previous != userName))
            {
                _vault.Remove(Resource, previous);
            }

            Clear(RecoveryResource);
            operation.Complete();
        }
        catch (Exception ex)
        {
            operation.Fail(ex, DiagnosticFailure.Credentials);
            if (ex is COMException or UnauthorizedAccessException or IOException)
            {
                throw new GitHubAuthException("Couldn't save the account to Windows Credential Locker. Try again.", new CredentialFailureException(ex));
            }

            throw;
        }
    }

    public void Clear()
    {
        using var operation = OperationDiagnostics.Begin(DiagnosticEvent.CredentialClear, DiagnosticArea.Auth);
        try
        {
            // Remove recovery copies first so a failed sign out cannot resurrect an old account.
            Clear(RecoveryResource);
            Clear(Resource);
            operation.Complete();
        }
        catch (Exception ex)
        {
            operation.Fail(ex, DiagnosticFailure.Credentials);
            if (ex is COMException or UnauthorizedAccessException or IOException)
            {
                throw new GitHubAuthException("Couldn't remove the account from Windows Credential Locker. Try again.", new CredentialFailureException(ex));
            }

            throw;
        }
    }

    private void Clear(string resource)
    {
        foreach (var userName in FindAll(resource))
        {
            _vault.Remove(resource, userName);
        }
    }

    private IReadOnlyList<string> FindAll(string resource)
    {
        try
        {
            return _vault.FindUserNames(resource);
        }
        catch (COMException ex) when (ex.HResult == NotFound)
        {
            // ERROR_NOT_FOUND is the vault's missing-resource result.
            return [];
        }
    }

}

// Preserve the failure chain for diagnostic deduplication without exposing vault details in user errors.
internal sealed class CredentialFailureException(Exception innerException) : Exception("Windows Credential Locker failed.", innerException)
{
    public override string ToString() => Message;
}
