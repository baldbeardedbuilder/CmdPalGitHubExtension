# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check notifications, browse repo issues and pull requests, explore agent tasks and codespaces, and open Actions from a repo. Saved queries land next.

## Issue and pull request updates

From an issue, you can close it as completed or not planned, reopen it, assign or remove yourself, and add or remove existing labels. Pull request actions include close or reopen, reviewer requests, assignees, and labels. Each change is confirmed against the current GitHub state before it is sent. Merged pull requests cannot be reopened or closed from the extension. Use the browser link for anything that needs a richer editor.

## Notifications

The notification menu keeps **Done** separate from issue state and thread subscriptions. You can subscribe to a thread, unsubscribe to return to repository notification rules, or ignore it to stop future notifications from that conversation. Ignoring does not mark a notification read or done. Issue changes from a notification refresh that preview from GitHub.

## Repositories

Open **Starred repositories** from the home page to browse your personal starred list. A repository's **Manage star** action shows your star state and lets you star or unstar it. This is your personal state, not the public star count. The GitHub token needs Starring access for these actions.

## Actions

You can cancel a running workflow from its **More** menu. Force cancel is a separate confirmed action, offered when a normal cancellation was accepted but the run remains active. An accepted request does not mean the run has stopped, so the extension checks GitHub for the final state.

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

## Contributing

Bugs, ideas, and pull requests are all welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)

Icons come from [GitHub Octicons](https://github.com/primer/octicons), also [MIT licensed](GitHubExtension/Assets/Octicons/LICENSE).
