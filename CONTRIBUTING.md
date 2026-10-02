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

### Running tests

```bash
dotnet test GitHubExtension.Tests/GitHubExtension.Tests.csproj -r win-x64
```

Tests never touch real GitHub or your Credential Locker. Auth tests use fakes, and the OAuth flow is exercised against a real loopback listener on `127.0.0.1`.

### Updating pages

Command Palette handles property and item notifications synchronously and may read the page from another thread before returning. Update your private state under its lock, then release the lock before setting toolkit properties, updating visible items, or calling `RaiseItemsChanged()` so those reads don't deadlock.

### Trying it in Command Palette

Open `GitHubExtension.slnx` in Visual Studio, set **GitHubExtension** as the startup project, and deploy it. Then open Command Palette and run **Reload Command Palette extensions**.

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
