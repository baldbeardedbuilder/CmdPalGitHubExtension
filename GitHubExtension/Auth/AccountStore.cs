// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Windows.Security.Credentials;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal interface IAccountStore
{
    GitHubAccount? Load();

    void Save(GitHubAccount account);

    void Clear();
}

/// <summary>
/// Keeps the signed in account in the Windows Credential Locker, so the token never touches disk in plain text.
/// </summary>
internal sealed class PasswordVaultAccountStore : IAccountStore
{
    private const string Resource = "BaldBeardedBuilder.CmdPal.GitHub";

    public GitHubAccount? Load()
    {
        var credentials = FindAll();
        if (credentials.Count == 0)
        {
            return null;
        }

        var credential = credentials[0];

        credential.RetrievePassword();
        var separator = credential.UserName.LastIndexOf('@');
        if (separator <= 0
            || !GitHubHost.TryParse(credential.UserName[(separator + 1)..], out var host)
            || string.IsNullOrEmpty(credential.Password))
        {
            return null;
        }

        return new GitHubAccount(host, credential.UserName[..separator], credential.Password);
    }

    public void Save(GitHubAccount account)
    {
        Clear();
        new PasswordVault().Add(new PasswordCredential(Resource, $"{account.Login}@{account.Host.Name}", account.Token));
    }

    public void Clear()
    {
        var vault = new PasswordVault();
        foreach (var credential in FindAll())
        {
            vault.Remove(credential);
        }
    }

    private static IReadOnlyList<PasswordCredential> FindAll()
    {
        try
        {
            return [.. new PasswordVault().FindAllByResource(Resource)];
        }
        catch (COMException)
        {
            // The vault throws when nothing is stored for the resource.
            return [];
        }
    }
}
