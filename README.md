# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check notifications, browse repo issues and pull requests, explore agent tasks and codespaces, and open Actions from a repo. Saved queries land next.

## Actions

Browse workflow runs from a repository's **Actions** view. Use **More > Cancel** on a running workflow. If cancellation is stuck, **More > Force cancel** opens a separate confirmation. Cancelling requires Actions write permission. A successful request does not mean the run has stopped, so the extension checks GitHub until it reports a terminal state. Eligible completed runs also offer **More > Rerun workflow** with a review step. If GitHub's response is unclear, check the run before submitting again.

## Codespaces

Starting a Codespace asks you to confirm because it uses compute time and may incur charges. Creating one has a review step that shows the repository, branch, and signed in account. GitHub may still be preparing a Codespace after accepting the request. If the result is unclear, the extension checks your Codespaces list. If it can't identify the new Codespace, another submission stays blocked.

Deleting a Codespace requires a separate confirmation with fresh git status. The extension checks that status again before deleting. If the Codespace or its reported changes shifted, you'll need to review the updated details before confirming.

## Copilot agent tasks

Starting an agent task includes a review step. When GitHub may have accepted a request but the response is unclear, check the repository's task list before trying again.

## Install

Releases aren't published yet. Until they are, build it yourself using the steps in [CONTRIBUTING.md](CONTRIBUTING.md).

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
