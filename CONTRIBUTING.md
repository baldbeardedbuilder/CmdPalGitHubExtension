# Contributing

Thanks for wanting to help build this. Whether it's a typo fix or a whole new page, you're welcome here.

## Code of Conduct

Everyone who participates agrees to follow our [Code of Conduct](CODE_OF_CONDUCT.md). Short version: be kind, assume good intent, and err on the side of grace.

## Getting set up

### What you need

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerToys](https://github.com/microsoft/PowerToys) with Command Palette turned on, for trying your changes for real
- Visual Studio 2022 or newer with the Windows application development workload, if you want to deploy and debug the MSIX package

### Building

MSIX packaging needs a Windows runtime identifier, so always pass one:

```bash
dotnet build GitHubExtension/GitHubExtension.csproj -r win-x64
```

If you want to test the github.com sign in, set up your own OAuth app first. The [installation guide](docs/src/content/docs/getting-started/installation.md#configure-githubcom-sign-in) walks through it. Enterprise sign in works without it.

#### Recovering from a locked executable

Build, Deploy, and Publish do not stop running extension processes. Command Palette can keep an executable locked, so a rebuild may fail until you explicitly stop the development instance.

1. Identify the locking instance in PowerShell:

   ```powershell
   Get-CimInstance Win32_Process -Filter "Name = 'BaldBeardedBuilder.GitHubExtension.exe'" |
       Select-Object ProcessId, ExecutablePath
   ```

2. Verify that its full executable path is the output of the checkout you are rebuilding, not another worktree or the installed MSIX package. If the path is unavailable, inspect it with appropriate permissions rather than guessing.
3. Prefer a graceful shutdown through the owning debugger or host. Closing the Command Palette window alone may leave the extension running. Stopping a shared host can also stop other extensions, so avoid it when preserving unrelated instances.
4. If graceful shutdown is unavailable or fails, recheck the path and PID immediately before explicitly forcing termination of only that instance with `taskkill /F /PID <verified-pid>`. Do not use image-name-wide termination (`/IM`) or process-tree termination (`/T`).
5. Retry the build, then redeploy and reload the extension as needed.

To verify worktree isolation on Windows, run an extension from worktree A and record its PID and executable path. Build, Deploy, and Publish from worktree B and confirm A's PID is still running at the same path. Repeat with an installed extension instance. Also try rebuilding a worktree whose own executable is locked: a lock failure must leave all instances running until you explicitly stop the verified development instance.

### Running tests

```bash
dotnet test GitHubExtension.Tests/GitHubExtension.Tests.csproj -r win-x64
```

Tests never touch real GitHub or your Credential Locker. Auth tests use fakes, and the OAuth flow is exercised against a real loopback listener on `127.0.0.1`.

### Updating pages

Use the destination page itself as a list item's command when navigating within Command Palette. The host doesn't handle `CommandResult.GoToPage`, so returning it from an invokable command won't open the page.

Command Palette handles property and item notifications synchronously and may read the page from another thread before returning. Update your private state under its lock, then release the lock before setting toolkit properties, updating visible items, or calling `RaiseItemsChanged()` so those reads don't deadlock.

Reuse the same `EmptyContent` item when its text and command haven't changed. Creating a new item on every `GetItems()` call can send the host into a notification loop, including when you're showing an error.

Use `ListLoadState` for request lifetimes. Refresh, account changes, and disposal cancel obsolete work; still check operation identity before applying results because clients can ignore cancellation. Let the operation finish before disposing its cancellation source, and keep cancellation callbacks outside state locks.

The provider owns its root pages and the HTTP clients it creates, not injected clients or auth services. Repository menus create their sections lazily and use weak reuse so old search results can be collected. Child pages keep their menu owner alive while the host holds them, and weak account subscriptions don't keep retired page graphs alive.

Don't dispose an active destination just because a search or notification refresh replaced its row. Account changes and owner disposal invalidate every live destination, cancel its requests, and detach subscriptions.

Notification refresh keeps submitted mutations alive. Deduplicate writes for the same notification and use `MutationExecutor` to reconcile uncertain results before retrying; don't show an unread or done change until GitHub confirms it.

### Trying it in Command Palette

Open `GitHubExtension.slnx` in Visual Studio, set **GitHubExtension** as the startup project, and deploy it. Then open Command Palette and run **Reload Command Palette extensions**.

## How to contribute

### Documentation site

The user documentation lives in `docs\` and uses Astro Starlight. Use **Node.js 24 LTS** and npm. From the repository root:

```powershell
Set-Location docs
npm ci
npm run dev
```

Open `http://localhost:4321/CmdPalGitHubExtension/`. The project URL prefix also applies locally, so links and search use the same paths as GitHub Pages.

Before submitting changes:

```powershell
npm run check
npm test
npm run build
npm run validate
```

The validator checks generated links, anchors, local assets, the Pages prefix, and Pagefind search artifacts. `npm test` checks the validator itself. To try the production build, run `npm run preview` and open the same URL. Search uses the generated production index, so check it in preview rather than relying only on the development server.

Write user guides in `docs\src\content\docs\` and update the sidebar in `docs\astro.config.mjs` when adding a page. Keep instructions aligned with the current extension, not planned features. The extension mark comes from `GitHubExtension\Assets\GHCmdPalMark.svg` under the repository's MIT license; `docs\public\favicon.svg` is a copy of that mark.

#### Enable GitHub Pages

A repository owner must select **Settings > Pages > Build and deployment > Source > GitHub Actions** once. Check that the `github-pages` environment permits deployment from `main`.

[The documentation workflow](.github/workflows/docs.yml) validates documentation pull requests without deploying them. Relevant changes pushed to `main` publish at `https://baldbeardedbuilder.github.io/CmdPalGitHubExtension/`. You can also run **Documentation** manually from `main` in Actions.

The workflow doesn't need OAuth or signing secrets. It uses the built-in GitHub token and the Pages deployment identity. Keep the root privacy policy and contributor/release guides as the canonical repository documents.

#### Dependency advisory

Astro currently brings in `http-cache-semantics@4.2.0`, which has an unpatched [shared-cache disclosure advisory](https://github.com/advisories/GHSA-ch52-4w7c-c8xp). `npm audit` reports it and its dependent packages. This site uses local images and deploys only static files to Pages, not an Astro server or a shared authenticated cache. Keep local previews bound to localhost, and review this advisory before adding credentialed remote image fetching or server rendering. Dependabot monitors the documentation dependencies for updates.

### Reporting bugs

Search [existing issues](https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/issues) first. If it's new, open one with the bug report template. Tell us what you expected, what happened, and how to make it happen again. Screenshots help a ton for UI problems.

Please don't post tokens, client secrets, or anything from your Credential Locker in an issue.

### Suggesting features

Open an issue with the feature request template. The most helpful part is the *why*: what are you trying to do, and what's getting in your way?

### Submitting changes

1. Fork the repo.
2. Pull the latest `main`, then create a branch from it.
3. Make your change and add tests for it.
4. Make sure the build and tests pass.
5. Commit using [conventional commits](#commit-messages).
6. Push and open a pull request against `main`.

### Commit messages

We use [Conventional Commits](https://www.conventionalcommits.org/):

```
type: short description

Optional body explaining why.
```

| Type | Use it for |
|------|-------------|
| `feat` | A new feature |
| `fix` | A bug fix |
| `docs` | Documentation only |
| `test` | Adding or updating tests |
| `refactor` | Code changes that don't fix a bug or add a feature |
| `ci` | Workflow and build pipeline changes |
| `chore` | Maintenance |

### Pull requests

- Keep each PR to one logical change. Small PRs get reviewed faster.
- CI builds and tests every PR, so make sure it's green.
- Explain what changed and why, and link the issue (`Fixes #123`).

## Project layout

```
GitHubExtension/
├── Assets/          # App icons and the GitHub mark
├── Auth/            # OAuth, PATs, hosts, and credential storage
├── Commands/        # Invokable commands like sign out
├── Pages/           # Command Palette pages and their adaptive cards
├── Properties/      # Assembly info and publish profiles
└── Icons.cs         # Shared icons

GitHubExtension.Tests/   # MSTest + Moq unit tests
```

## License

By contributing, you agree your contributions are licensed under the [MIT License](LICENSE).
