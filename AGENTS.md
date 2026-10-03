# Agent guide

Context for AI agents working in this repo. Read this before making changes.

## What this is

A Microsoft Command Palette extension that brings GitHub into Command Palette:
notifications, repos, issues, pull requests, agent tasks, codespaces, and Actions.
C# on .NET 10, packaged as MSIX, built on WinRT via CsWinRT.

## Build and test

Build and test on Windows with PowerShell. Always pass a Windows runtime identifier.
MSIX packaging breaks without one.

```powershell
dotnet build GitHubExtension\GitHubExtension.csproj -r win-x64
dotnet test GitHubExtension.Tests\GitHubExtension.Tests.csproj -r win-x64
```

Tests use MSTest + Moq. They never touch real GitHub or the Credential Locker.
Auth tests use fakes; the OAuth flow runs against a real loopback listener on `127.0.0.1`.

### Hosted Copilot sessions

`.github/workflows/copilot-setup-steps.yml` selects `windows-latest`, installs .NET 10,
and restores application and test dependencies with `win-x64`. Copilot uses this setup
after the workflow is merged into `main`. Run the build and tests above after making changes.

GitHub's integrated Copilot firewall doesn't support Windows. Before starting a hosted
session, disable it in the repository's Copilot Internet access settings. This runner
has no separate network controls, so keep OAuth, signing, and other credentials out of
Agents secrets. Builds and tests don't need them. Actions secrets aren't automatically
passed to the agent; secrets from the former Actions `copilot` environment were migrated
to Agents, so check that section too.

## Command Palette rules

These are not obvious and will bite you. Follow them.

- **Threading:** the host handles property and item notifications synchronously and
  may read the page from another thread before returning. Update private state under
  its lock, then release the lock before setting toolkit properties, updating visible
  items, or calling `RaiseItemsChanged()`. Otherwise you deadlock.
- **Navigation:** the host does not handle `CommandResult.GoToPage`. To navigate within
  Command Palette, use the destination page itself as the list item's command.
- **Empty content:** reuse the same `EmptyContent` item when its text and command have
  not changed. Creating a new one on every `GetItems()` call can send the host into a
  notification loop, including when showing an error.

## Auth and secrets

- Tokens live in Windows Credential Locker, never on disk.
- github.com sign in needs an OAuth app. Local builds supply their own via the
  `GITHUB_OAUTH_CLIENT_ID` / `GITHUB_OAUTH_CLIENT_SECRET` env vars or a gitignored
  `GitHubExtension/oauth.local.props`. Enterprise sign in works without it.
- Never commit tokens, client secrets, or Credential Locker contents.
- Use typed operation diagnostics and route templates, never actual API hosts or paths.
  Logs exclude tokens, OAuth values, prompts, bodies, search text, raw exception messages,
  and arbitrary response headers. Keep user-facing errors separate from logs.

## Project layout

```
GitHubExtension/
├── Actions/         # Workflow run browsing
├── Agents/          # Copilot cloud agent tasks (Agent Tasks API)
├── Api/             # GitHub REST client plumbing
├── Auth/            # OAuth, PATs, hosts, credential storage
├── Codespaces/      # Codespace listing and lifecycle
├── Commands/        # Invokable commands like sign out
├── Issues/          # Issue lists and detail pages
├── Notifications/   # Notification list and PR previews
├── Pages/           # Command Palette pages and adaptive cards
├── PullRequests/    # Pull request lists and detail pages
├── Repositories/    # Repo search and the repo menu
└── Icons.cs         # Shared icons

GitHubExtension.Tests/   # MSTest + Moq unit tests
```

## Conventions

- **Commits:** Conventional Commits (`feat`, `fix`, `docs`, `test`, `refactor`, `ci`,
  `chore`). Keep each PR to one logical change and link the issue (`Fixes #123`).
- **Branching:** pull latest `main`, then branch from it. PRs target `main`.
- **Add tests** for your change and make sure build and tests pass before opening a PR.
- **No emdashes** in prose. Use a plain dash or rework the sentence.
