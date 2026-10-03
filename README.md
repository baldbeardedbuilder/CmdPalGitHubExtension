# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check notifications, browse repo issues and pull requests, explore agent tasks and codespaces, and open Actions from a repo. Saved queries land next.

## Actions

Browse workflow runs from a repository's **Actions** view. Use **More > Cancel** on a running workflow. If cancellation is stuck, **More > Force cancel** opens a separate confirmation. Cancelling requires Actions write permission. GitHub accepts the request before the run stops, so the extension refreshes status until GitHub reports a terminal state. Eligible completed runs also offer **More > Rerun workflow** with a review step before submission.

## Filtering and search

In a repository's **Issues** or **Pull Requests** view, typing filters the rows you've loaded, together with your **Open** or **Closed** selection. It doesn't search the whole repository. When more pages are available, you'll see the loaded-results scope and a **Load more** action, even if nothing on the current pages matches. Each click fetches one page and keeps your filters.

In **Repos**, typing first filters your loaded personal repositories, then searches the repositories you can access on your GitHub host. Use **Load more** to fetch another search page. GitHub's search API exposes at most 1,000 results per query. If you hit that limit, narrow your search to reach the repositories you need.

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
