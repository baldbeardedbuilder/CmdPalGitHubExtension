---
title: Privacy
description: Learn where credentials are stored and what the extension logs.
---

The extension doesn't collect telemetry or analytics. It sends requests from your device to the GitHub host you sign in to.

## Credentials

Your host, username, and access token are stored in **Windows Credential Locker**, not a plain file. Signing out deletes those stored credentials.

During github.com sign-in, the extension briefly listens on `127.0.0.1` for the browser redirect. That listener is only reachable from your machine.

## Local diagnostics

Operation diagnostics use Command Palette's local logs. They include allowlisted categories, generated operation IDs, timing, outcomes, HTTP status codes, safe method names, and route templates.

They exclude tokens, OAuth values, actual API hosts and paths, repository and account names, arbitrary response headers, prompts, search text, bodies, and raw exception messages. Successful read logging requires opting into verbose diagnostics, with the same privacy rules.

Review logs and screenshots before sharing them.

## Read the policy

The repository's [Privacy Policy](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/blob/main/PRIVACY_POLICY.md) is the canonical policy. GitHub's handling of requests is covered by its privacy statement, or your company's policies for Enterprise.

This documentation site doesn't add analytics or a third-party search service. Its search index is generated with the site; hosting requests are handled by GitHub Pages.
