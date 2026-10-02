# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check your notifications and repos. Agents, codespaces, and saved queries land next.

## What works today

- Sign in to github.com through your browser. No tokens to copy and paste.
- Sign in to GitHub Enterprise Server with your server URL and a personal access token.
- Your token lives in Windows Credential Locker, not in a file on disk.
- Browse your notifications with issue and PR state, open issue details in Command Palette, filter as you type, and mark them read or done.
- Preview a pull request's description, branches, labels, and change counts beside its notification. Enter still opens it on GitHub.
- Find repos fast. Yours filter instantly, and pausing on a search checks all of GitHub too. Qualifiers like `user:` and `language:` work.
- Pick **Actions** from a repo's **More** menu to browse workflow runs. Filter by workflow, run title, actor, or status, refresh the list, and open a run on GitHub.

## Install

Releases aren't published yet. Until they are, build it yourself using the steps in [CONTRIBUTING.md](CONTRIBUTING.md).

## Signing in

Open Command Palette, type **GitHub**, and pick it.

**github.com:** Click **Sign in with GitHub**. Your browser opens, you approve the app, and you're back in Command Palette. The extension listens on `127.0.0.1` for a few minutes to catch GitHub's redirect, then stops.

**GitHub Enterprise Server:** Click **Sign in with GitHub Enterprise account**, enter your server URL (like `https://github.example.com`), and paste a [personal access token](https://docs.github.com/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens). Give the token the `repo`, `read:org`, `notifications`, and `codespace` scopes so future features have what they need.

To sign out, open the extension and pick **Sign out**.

## Building with your own OAuth app

The github.com sign in needs an OAuth app. Official builds have one baked in. Local builds need yours.

1. Go to [Settings > Developer settings > OAuth Apps](https://github.com/settings/developers) and click **New OAuth App**.
2. Set **Authorization callback URL** to `http://127.0.0.1/callback`. GitHub lets loopback redirects use any port, so you don't need to pick one.
3. Create a client secret.
4. Hand the values to the build in one of two ways:

   - Environment variables `GITHUB_OAUTH_CLIENT_ID` and `GITHUB_OAUTH_CLIENT_SECRET`.
   - A `GitHubExtension/oauth.local.props` file (it's gitignored):

     ```xml
     <Project>
       <PropertyGroup>
         <GitHubOAuthClientId>your client id</GitHubOAuthClientId>
         <GitHubOAuthClientSecret>your client secret</GitHubOAuthClientSecret>
       </PropertyGroup>
     </Project>
     ```

If you skip this, everything still builds. The github.com button just tells you OAuth isn't configured, and Enterprise sign in keeps working.

Yes, the client secret ships inside the app. That's normal for desktop OAuth apps since there's nowhere safe to hide it, and it's why the flow also uses PKCE.

## Contributing

Bugs, ideas, and pull requests are all welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md).

## Contributors

<!-- ALL-CONTRIBUTORS-LIST:START - Do not remove or modify this section -->
<!-- prettier-ignore-start -->
<!-- markdownlint-disable -->
<table>
  <tbody>
    <tr>
      <td align="center" valign="top" width="14.28%"><a href="https://baldbeardedbuilder.com/"><img src="https://avatars.githubusercontent.com/u/1228996?v=4?s=100" width="100px;" alt="Michael Jolley"/><br /><sub><b>Michael Jolley</b></sub></a><br /><a href="https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/commits?author=michaeljolley" title="Code">💻</a> <a href="https://github.com/baldbeardedbuilder/CmdPalGitHubExtension/commits?author=michaeljolley" title="Tests">⚠️</a> <a href="#design-michaeljolley" title="Design">🎨</a></td>
    </tr>
  </tbody>
</table>

<!-- markdownlint-restore -->
<!-- prettier-ignore-end -->

<!-- ALL-CONTRIBUTORS-LIST:END -->

## License

[MIT](LICENSE)

Icons come from [GitHub Octicons](https://github.com/primer/octicons), also [MIT licensed](GitHubExtension/Assets/Octicons/LICENSE).
