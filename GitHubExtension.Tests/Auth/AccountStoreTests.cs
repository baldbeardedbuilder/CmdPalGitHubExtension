// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.InteropServices;

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests.Auth;

[TestClass]
public class AccountStoreTests
{
    internal const string Resource = "BaldBeardedBuilder.CmdPal.GitHub";
    internal const string RecoveryResource = Resource + ".Recovery";
    internal static readonly GitHubAccount Saved = new(GitHubHost.GitHubDotCom, "octocat", "old-token");
    internal static readonly GitHubAccount Replacement = Saved with { Token = "new-token" };

    [TestMethod]
    public void Load_MissingResource_ReturnsNull()
    {
        Assert.IsNull(new PasswordVaultAccountStore(new FakeCredentialVault()).Load());
    }

    [TestMethod]
    [DataRow(unchecked((int)0x80070005))]
    [DataRow(unchecked((int)0x80004005))]
    [DataRow(unchecked((int)0x80070002))]
    public void Load_OtherComFailure_SurfacesSafeError(int hresult)
    {
        var vault = new FakeCredentialVault
        {
            FindFailure = _ => new COMException("sensitive vault details", hresult),
        };

        var error = Assert.Throws<GitHubAuthException>(() => new PasswordVaultAccountStore(vault).Load());

        StringAssert.Contains(error.Message, "Couldn't read");
        Assert.IsFalse(error.ToString().Contains("sensitive vault details", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Load_LegacyCredential_ReturnsAccount()
    {
        var vault = new FakeCredentialVault(Saved);

        Assert.AreEqual(Saved, new PasswordVaultAccountStore(vault).Load());
    }

    [TestMethod]
    public void Load_RecoveryLookupFailure_IsNotTreatedAsSignedOut()
    {
        var vault = new FakeCredentialVault
        {
            FindFailure = resource => resource == RecoveryResource ? new COMException() : null,
        };

        Assert.Throws<GitHubAuthException>(() => new PasswordVaultAccountStore(vault).Load());
    }

    [TestMethod]
    public void Load_AccessDenied_SurfacesSafeError()
    {
        var vault = new FakeCredentialVault
        {
            FindFailure = _ => new UnauthorizedAccessException("sensitive vault details"),
        };

        var error = Assert.Throws<GitHubAuthException>(() => new PasswordVaultAccountStore(vault).Load());

        Assert.IsFalse(error.ToString().Contains("sensitive vault details", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Load_RetrieveFailure_IsNotTreatedAsSignedOut()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            RetrieveFailure = (_, _) => throw new COMException("missing password", unchecked((int)0x80070490)),
        };

        Assert.Throws<GitHubAuthException>(() => new PasswordVaultAccountStore(vault).Load());
    }

    [TestMethod]
    public void Save_NewIdentityWriteFailure_PreservesExistingAccount()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            AddFailure = (resource, _) =>
            {
                if (resource == Resource)
                {
                    throw new COMException();
                }
            },
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(() => store.Save(Replacement with { Login = "mona" }));

        Assert.AreEqual(Saved, store.Load());
        Assert.AreEqual(1, vault.Credentials.Count);
    }

    [TestMethod]
    public void Save_SameIdentityDestructiveWriteFailure_PreservesRecoverableAccount()
    {
        var vault = new FakeCredentialVault(Saved);
        var store = new PasswordVaultAccountStore(vault);
        vault.AddFailure = (resource, _) =>
        {
            if (resource == Resource)
            {
                throw new COMException();
            }
        };

        Assert.Throws<GitHubAuthException>(() => store.Save(Replacement));

        Assert.AreEqual(Saved, new PasswordVaultAccountStore(vault).Load());
        Assert.IsTrue(vault.Credentials.Keys.All(key => key.Resource == RecoveryResource));

        vault.AddFailure = null;
        store.Save(Replacement);

        Assert.AreEqual(Replacement, store.Load());
        Assert.AreEqual(1, vault.Credentials.Count);
        Assert.IsTrue(vault.Credentials.Keys.All(key => key.Resource == Resource));
    }

    [TestMethod]
    public void Save_BackupWriteFailure_LeavesPrimaryUntouched()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            AddFailure = (_, _) => throw new COMException(),
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(() => store.Save(Replacement));

        Assert.AreEqual(Saved, store.Load());
    }

    [TestMethod]
    public void Save_OldAccountRemovalFailure_LeavesAccountsRecoverable()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            RemoveFailure = (_, _) => throw new COMException(),
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(() => store.Save(Replacement with { Login = "mona" }));

        Assert.AreEqual(Saved, store.Load());
        Assert.AreEqual(2, vault.Credentials.Count);
    }

    [TestMethod]
    public void Save_BackupReadFailure_LeavesPrimaryUntouched()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            RetrieveFailure = (_, _) => throw new COMException(),
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(() => store.Save(Replacement));

        vault.RetrieveFailure = null;
        Assert.AreEqual(Saved, store.Load());
        Assert.AreEqual(1, vault.Credentials.Count);
    }

    [TestMethod]
    public void Save_RecoveryCleanupFailure_PrefersPersistedReplacement()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            RemoveFailure = (resource, _) =>
            {
                if (resource == RecoveryResource)
                {
                    throw new COMException();
                }
            },
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(() => store.Save(Replacement));

        Assert.AreEqual(Replacement, store.Load());
        Assert.AreEqual(2, vault.Credentials.Count);
    }

    [TestMethod]
    public void Save_DifferentIdentity_RemovesPreviousAccount()
    {
        var vault = new FakeCredentialVault(Saved);
        var store = new PasswordVaultAccountStore(vault);
        var replacement = Replacement with { Login = "mona" };

        store.Save(replacement);

        Assert.AreEqual(replacement, store.Load());
        Assert.AreEqual(1, vault.Credentials.Count);
    }

    [TestMethod]
    public void Save_FindFailure_DoesNotMutateVault()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            FindFailure = _ => new COMException(),
        };

        Assert.Throws<GitHubAuthException>(() => new PasswordVaultAccountStore(vault).Save(Replacement));

        vault.FindFailure = null;
        Assert.AreEqual(Saved, new PasswordVaultAccountStore(vault).Load());
    }

    [TestMethod]
    public void Clear_FindFailure_SurfacesErrorAndPreservesAccount()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            FindFailure = _ => new COMException(),
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(store.Clear);

        vault.FindFailure = null;
        Assert.AreEqual(Saved, store.Load());
    }

    [TestMethod]
    public void Clear_RemoveFailure_PreservesAccount()
    {
        var vault = new FakeCredentialVault(Saved)
        {
            RemoveFailure = (_, _) => throw new COMException(),
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(store.Clear);

        Assert.AreEqual(Saved, store.Load());
    }

    [TestMethod]
    public void Clear_RecoveryRemovalFailure_DoesNotRemovePrimary()
    {
        var vault = new FakeCredentialVault(Saved);
        vault.Add(RecoveryResource, $"{Saved.Login}@{Saved.Host.Name}", Saved.Token);
        vault.RemoveFailure = (resource, _) =>
        {
            if (resource == RecoveryResource)
            {
                throw new COMException();
            }
        };
        var store = new PasswordVaultAccountStore(vault);

        Assert.Throws<GitHubAuthException>(store.Clear);

        Assert.AreEqual(Saved, store.Load());
        Assert.AreEqual(2, vault.Credentials.Count);
    }

    [TestMethod]
    public void Clear_RemovesPrimaryAndRecoveryAndIsIdempotent()
    {
        var vault = new FakeCredentialVault(Saved);
        vault.Add(RecoveryResource, $"{Saved.Login}@{Saved.Host.Name}", Saved.Token);
        var store = new PasswordVaultAccountStore(vault);

        store.Clear();
        store.Clear();

        Assert.IsNull(store.Load());
        Assert.AreEqual(0, vault.Credentials.Count);
    }
}

internal sealed class FakeCredentialVault : ICredentialVault
{
    public FakeCredentialVault(GitHubAccount? initial = null)
    {
        if (initial is not null)
        {
            Add(AccountStoreTests.Resource, $"{initial.Login}@{initial.Host.Name}", initial.Token);
        }
    }

    public Dictionary<(string Resource, string UserName), string> Credentials { get; } = [];

    public Func<string, Exception?>? FindFailure { get; set; }

    public Action<string, string>? RetrieveFailure { get; set; }

    public Action<string, string>? AddFailure { get; set; }

    public Action<string, string>? RemoveFailure { get; set; }

    public IReadOnlyList<string> FindUserNames(string resource)
    {
        if (FindFailure?.Invoke(resource) is { } failure)
        {
            throw failure;
        }

        var names = Credentials.Keys.Where(key => key.Resource == resource).Select(key => key.UserName).ToArray();
        return names.Length == 0
            ? throw new COMException("Element not found.", unchecked((int)0x80070490))
            : names;
    }

    public string RetrievePassword(string resource, string userName)
    {
        RetrieveFailure?.Invoke(resource, userName);
        return Credentials[(resource, userName)];
    }

    public void Add(string resource, string userName, string password)
    {
        // Model a destructive same-key replacement, not an unsupported atomic vault transaction.
        Credentials.Remove((resource, userName));
        AddFailure?.Invoke(resource, userName);
        Credentials[(resource, userName)] = password;
    }

    public void Remove(string resource, string userName)
    {
        RemoveFailure?.Invoke(resource, userName);
        Credentials.Remove((resource, userName));
    }
}
