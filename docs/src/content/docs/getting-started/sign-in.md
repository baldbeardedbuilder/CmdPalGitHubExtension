---
title: Sign in
description: Connect to github.com through your browser or use a token for a GitHub Enterprise account.
---

Open Command Palette, type **GitHub**, and select the extension.

## github.com

1. Choose **Sign in with GitHub**.
2. In your browser, check which GitHub account is active and approve the OAuth app.
3. Return to Command Palette when sign-in finishes.

The extension briefly listens on `127.0.0.1` to receive the browser redirect. Sign-in times out after five minutes and the listener stops. If the request expires, start again from Command Palette.

:::tip[Missing OAuth configuration]
If this build doesn't have a GitHub OAuth app configured, follow the [installation guide](/CmdPalGitHubExtension/getting-started/installation/#configure-githubcom-sign-in), rebuild, and redeploy. Setting environment variables after the build won't update an already-built application.
:::

The OAuth request asks for `repo`, `read:org`, `notifications`, and `codespace` scopes. Approving it doesn't override your organization's policies or give you access to repositories you couldn't already use.

## GitHub Enterprise

1. Choose **Sign in with GitHub Enterprise account**.
2. Enter your host, such as `https://github.example.com`.
3. Paste a [personal access token](https://docs.github.com/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) for that host and submit the form.

Use your host's web address, not an `/api/v3` URL. The extension requires HTTPS and also recognizes `ghe.com` data-residency hosts.

For a classic token, the current sign-in guidance is `repo`, `read:org`, `notifications`, and `codespace`. Available scopes depend on your host. Follow your organization's token policy, and use a token with access to the repositories you need. Fine-grained tokens need the corresponding feature permissions and repository selection.

Some features need more than a valid token. Agent tasks depend on Copilot access and Agent tasks permissions. Codespaces is only supported on github.com by this extension, and async pull request merging isn't verified on Enterprise hosts.

## Organization access

If GitHub returns an organization single sign-on authorization link, use it and then refresh the affected page. Signing in successfully doesn't necessarily authorize your token for every organization.

## Sign out or change accounts

On the extension's home page, open a row's **More** menu and choose **Sign out**. Then sign in with the account and host you want to use.

Signing out removes the stored credentials. It also cancels local outstanding work and discards old account results, but it can't undo a request GitHub has already accepted.

See [privacy](/CmdPalGitHubExtension/reference/privacy/) for storage and logging details.
