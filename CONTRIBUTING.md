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

If you want to test the github.com sign in, set up your own OAuth app first. The [README](README.md#building-with-your-own-oauth-app) walks through it. Enterprise sign in works without it.

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

### Trying it in Command Palette

Open `GitHubExtension.slnx` in Visual Studio, set **GitHubExtension** as the startup project, and deploy it. Then open Command Palette and run **Reload Command Palette extensions**.

## Preparing a release

Stable `vMAJOR.MINOR.PATCH` tags trigger the release workflow. It stamps a four-part package version in the workflow checkout, runs Release tests, validates x64 and ARM64 packages, signs GitHub downloads, and submits WinGet and Microsoft Store updates. It does not commit version changes to `main`.

See [RELEASING.md](RELEASING.md) for the required protected environments and channel configuration. To dry-run packaging without distribution credentials, use a disposable checkout on a matching Windows x64 or ARM64 machine. Set the manifest identity version to a test version such as `1.2.3.0`, then run:

```powershell
dotnet restore GitHubExtension/GitHubExtension.csproj -r win-x64 -p:Platform=x64
dotnet publish GitHubExtension/GitHubExtension.csproj -c Release -r win-x64 --no-restore -p:Platform=x64 -p:PublishProfile=win-x64
```

Repeat on a Windows ARM64 machine using `win-arm64`, `Platform=ARM64`, and `PublishProfile=win-arm64`. Unpack the resulting MSIX with the Windows SDK's `makeappx.exe` and confirm its manifest identity, publisher, version, and architecture, plus the packaged executable and Release assembly versions. Restore the manifest afterward if this is not a disposable checkout. Release automation performs these checks in its workflow jobs.

## How to contribute

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
