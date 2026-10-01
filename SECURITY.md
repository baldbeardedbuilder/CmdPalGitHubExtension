# Security Policy

## Supported versions

Only the latest release gets security fixes.

## Reporting a vulnerability

**Please don't report security problems in public issues.**

Use [GitHub's private vulnerability reporting](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/security/advisories/new) instead. Include:

- What the problem is
- How to reproduce it
- What someone could do with it
- A suggested fix, if you have one

Never include a real token or client secret in a report. A redacted example is plenty.

## What to expect

- We'll acknowledge your report within 48 hours.
- Within a week, we'll tell you whether we can reproduce it and what the plan is.
- We'll ship a fix as fast as the severity calls for.

## Scope

This extension handles GitHub credentials, so we care a lot about:

- Token leaks, whether through logs, files, crash output, or network calls to anywhere other than your GitHub host
- Flaws in the OAuth flow, like state or PKCE validation, or the loopback listener accepting things it shouldn't
- Problems with how tokens are stored in Windows Credential Locker
- Vulnerable dependencies

Thanks for helping keep everyone's accounts safe.
