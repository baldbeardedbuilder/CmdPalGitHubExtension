---
title: Codespaces
description: Open and manage your Codespaces, with confirmations for compute and permanent deletion.
---

Open **GitHub > Codespaces** to see your development environments. Select one to open it in your browser. Type to filter loaded rows by repository, name, display name, branch, or state.

:::note[Host support]
The extension supports Codespaces on **github.com**, not GitHub Enterprise Server or other Enterprise hosts.
:::

## Start or close an environment

In a Codespace row's **More** menu:

- A stopped environment offers **Start Codespace**, with an account, host, target, and billing confirmation.
- An available environment offers **Close Codespace** to stop its compute.
- **Copy URL**, **Copy name**, and **Refresh** help you open or check the environment elsewhere.

Starting compute can incur charges. Closing compute doesn't delete the environment or remove its storage.

## Create a Codespace

1. On the home page's **Codespaces** row, open **More > Create Codespace**. Existing Codespace rows offer it too.
2. Enter a repository as `owner/name`.
3. Optionally enter a branch. Leave it blank for the repository's default branch.
4. Select **Review creation**.
5. Check the account, host, repository, branch, and billing warning before confirming.

After creation, **Open Codespace** opens the new environment. GitHub may still be preparing it.

## Delete a Codespace

Choose **More > Delete Codespace**. Review the current environment and any reported uncommitted, unpushed, or ahead/behind changes.

Deletion is permanent. Push or back up your work before choosing **Permanently delete this codespace**. Unknown change status isn't proof that your work is safe.

## If creation is blocked

A lost response can leave the page at **Creation is blocked**. Use **Check Codespaces on GitHub** and refresh your list, but don't create another environment just because the first one isn't visible yet. A queued creation could appear later and cause duplicate charges.

The page keeps creation blocked when it can't prove retry safety. Read [request safety](/CmdPalGitHubExtension/reference/request-safety/) for why.
