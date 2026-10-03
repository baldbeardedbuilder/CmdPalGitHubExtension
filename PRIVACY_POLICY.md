# Privacy Policy

**GitHub extension for Microsoft Command Palette**

*Last updated: October 3, 2026*

## The short version

We don't collect anything. No telemetry, no analytics, no tracking. The extension talks to GitHub on your behalf and nobody else.

## What the extension stores

When you sign in, the extension saves your GitHub host, username, and access token in [Windows Credential Locker](https://learn.microsoft.com/windows/apps/develop/security/credential-locker) on your device. It stays there until you sign out, which deletes it.

Credentials aren't written to plain files. Operation diagnostics use Command Palette's local logs and include only allowlisted event and failure categories, severity, generated operation IDs, timing, outcomes, HTTP status codes, safe method names, and route templates. Auth, credential-store, network, and schema failures are covered. Successful reads require opting into verbose diagnostics, with the same privacy rules. Actual API hosts and paths, repository and account names, arbitrary response headers, tokens, OAuth values, prompts, search text, bodies, and raw exception messages aren't logged.

Nothing leaves your machine except requests to GitHub. Review logs before sharing them.

## Who the extension talks to

- **github.com**, or the **GitHub Enterprise Server** you sign in to. Requests go straight from your device to that host using your token. GitHub's handling of that data is covered by the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement), or your company's policies for Enterprise Server.
- **127.0.0.1** during github.com sign in. The extension briefly listens on a local port to receive GitHub's redirect, then shuts the listener down. It's only reachable from your own machine.

## Sharing

We don't have your data, so there's nothing to share.

## Changes

If this policy changes, the new version will be posted in this repository.

## Questions

Open an issue on the [GitHub repository](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/issues).
