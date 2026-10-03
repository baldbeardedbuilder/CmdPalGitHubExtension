---
title: Troubleshooting
description: Recover from sign-in, permission, loading, and uncertain-request problems.
---

Start with the visible error and the signed-in account/host. Repeating a write isn't a useful first step when GitHub may already be processing it.

## The extension doesn't appear

Confirm Command Palette is enabled in PowerToys. Deploy the project, then run **Reload Command Palette extensions** in Command Palette and search for **GitHub** again.

If deployment or a rebuild fails, follow the [installation guide](/CmdPalGitHubExtension/getting-started/installation/) and the contributor guide's [locked-executable recovery](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/blob/main/CONTRIBUTING.md#recovering-from-a-locked-executable). Don't stop every extension process; another checkout or installed copy may be in use.

## This build doesn't have a GitHub OAuth app configured

Both the client ID and client secret must be present **when you build**. Follow [OAuth setup](/CmdPalGitHubExtension/getting-started/installation/#configure-githubcom-sign-in), rebuild, and deploy that build. Enterprise token sign-in doesn't require an OAuth app.

## Browser sign-in timed out

Start sign-in again and finish the approval within five minutes. Check that your browser can reach GitHub and return to the `127.0.0.1` loopback callback. Don't post the callback URL with its query string in an issue; it contains OAuth values.

## GitHub denied access

Check the token's expiration, repository access, and feature permissions. For organizations using single sign-on, follow the authorization link when the extension offers one, then refresh.

Agent task access depends on Copilot permissions and organization policy. Codespaces isn't supported on Enterprise hosts by this extension. Merging and Actions mutations need write access; a successful read doesn't prove you have it.

## No matching items

Most lists only filter loaded rows. Use **Load more** in repository search, Issues, or Pull Requests when offered. Check [filtering and search](/CmdPalGitHubExtension/reference/filtering/) before assuming the repository has no matches.

## Requests won't load

Check network access to your signed-in GitHub host, then refresh the affected page. If GitHub reports a rate limit, wait before retrying. For an expired or revoked token, sign out and sign back in.

## A request is pending or its outcome is unknown

Check the target on GitHub and use the page's refresh/status actions. A timeout doesn't prove the write failed, and a missing list entry doesn't prove queued creation won't appear later. Read [request safety](/CmdPalGitHubExtension/reference/request-safety/) before submitting again.

## Report a bug safely

Use the repository's [bug report form](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/issues/new?template=bug_report.yml). Include what you expected, what happened, reproduction steps, and whether you're using github.com or Enterprise.

Screenshots help, but remove private repository names and other sensitive content. Never share tokens, OAuth client secrets, prompts, callback query strings, or Credential Locker contents. Review any local logs before attaching them.
