# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check notifications, browse repo issues and pull requests, explore agent tasks and codespaces, and open Actions from a repo. Saved queries land next.

## Documentation

Start with the [documentation site](https://baldbeardedbuilder.github.io/CmdPalGitHubExtension/) for installation, sign-in, feature guides, and troubleshooting. The site publishes through GitHub Pages once Pages is enabled and the documentation workflow is on `main`.

The documentation source lives in [`docs`](docs). See [Contributing](CONTRIBUTING.md#documentation-site) to preview or update it.

## Install

Releases aren't published yet. Until they are, follow the [installation guide](docs/src/content/docs/getting-started/installation.md) to build, configure sign-in, and deploy it.

## Signing in

Open Command Palette, type **GitHub**, and pick it.

**github.com:** Click **Sign in with GitHub**. Your browser opens, you approve the app, and you're back in Command Palette. The extension listens on `127.0.0.1` for a few minutes to catch GitHub's redirect, then stops.

**GitHub Enterprise Server:** Click **Sign in with GitHub Enterprise account**, enter your server URL (like `https://github.example.com`), and paste a [personal access token](https://docs.github.com/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens). Give the token the `repo`, `read:org`, `notifications`, and `codespace` scopes so future features have what they need.

To sign out, open the extension and pick **Sign out**.

## Pin your GitHub destinations

Use a destination's **More** menu to pin **GitHub**, **Notifications**, **Repos**, **Agents**, or **Codespaces** to Command Palette Home or the Dock. Inside a repository, you can also pin **Issues**, **Pull Requests**, **Copilot tasks**, and **Actions**. Each Dock pin is one button that opens its page.

General pins follow your current account. Repository pins require the account and host that created them, even after a restart. Pins don't save filters, search text, or form inputs. Saved queries, detail pages, creation forms, and one-time actions aren't pinnable.

## Contributing

Bugs, ideas, and pull requests are all welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)

Icons come from [GitHub Octicons](https://github.com/primer/octicons), also [MIT licensed](GitHubExtension/Assets/Octicons/LICENSE).
