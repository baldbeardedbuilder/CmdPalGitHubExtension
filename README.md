# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check notifications, browse repo issues and pull requests, explore agent tasks and codespaces, and open Actions from a repo. Saved queries land next.

## Actions

Browse workflow runs from a repository's **Actions** view. Use **More > Cancel** on a running workflow. If cancellation is stuck, **More > Force cancel** opens a separate confirmation. Cancelling requires Actions write permission. GitHub accepts the request before the run stops, so the extension refreshes status until GitHub reports a terminal state. Eligible completed runs also offer **More > Rerun workflow** with a review step before submission.

## Mutation safety

Starting and creating a Codespace first shows a confirmation with the signed-in account, host, target, and billing implications. Repeated submissions do not send duplicate writes. Signing out or switching accounts cancels outstanding work and discards old results. Notification changes refresh the inbox from GitHub rather than claiming success optimistically. Errors remain visible, including organization SSO authorization links when GitHub supplies one.

Accepted requests can still be processing. Refresh to check the authoritative state before retrying. If a creation request times out or its outcome cannot be verified, creation stays blocked on that page to prevent duplicate environments. You can inspect your Codespaces on GitHub, but absence from the list cannot prove a queued creation will not appear later. Without an exact authoritative reconciliation, the extension cannot safely retry that creation and does not offer an acknowledgement override.

### Adding mutation commands

Use the shared mutation executor and confirmation cards. Require confirmation for deletion, merge, compute start/create, workflow rerun/dispatch, and agent submission. Capture the account, host, and target; validate fresh permission and target state before submitting. Reconciliation must use authoritative reads to verify the requested fields, preserve pending/unknown outcomes when reads fail, and explicitly prove retry safety before resending non-idempotent requests.

The executor accepts an optional caller cancellation token linked to the account session. Cancellation before submission is safe to retry; cancellation after submission leaves the outcome unknown because it cannot roll back GitHub's work.

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
