# GitHub for Command Palette

Your notifications, repos, agents, codespaces, and saved queries, one keystroke away. This extension brings GitHub into [Microsoft Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) so you can check on your work without opening a browser tab you'll forget to close.

> [!NOTE]
> This is early. Sign in, then check notifications, browse repo issues and pull requests, explore agent tasks and codespaces, and open Actions from a repo. Saved queries land next.

## What works today

- Sign in to github.com through your browser. No tokens to copy and paste.
- Sign in to GitHub Enterprise Server with your server URL and a personal access token.
- Your token lives in Windows Credential Locker, not in a file on disk.
- Browse your notifications with issue and PR state, open issue details in Command Palette, filter as you type, and mark them read or done.
- Preview a pull request's description, branches, labels, and change counts beside its notification. Enter still opens it on GitHub.
- Find repos fast. Yours filter instantly, and pausing on a search checks all of GitHub too. Qualifiers like `user:` and `language:` work.
- Select a repo to open its menu with Issues, Pull Requests, Actions, and Discussions. Issues and pull requests open in Command Palette; discussions open on GitHub. Use **More > Open on GitHub** to skip the menu.
- Filter repository issue and pull request lists as you type, see issue labels and pull request status, and page through results.
- Check your Copilot cloud agent tasks, newest activity first, with repository names, models, and status badges. Filter by title, repo, model, or status, press Enter to open a task, or use More to copy its URL and refresh.
- Pick **Actions** from the repository menu or a repo's **More** menu to browse workflow runs. Filter by workflow, run title, actor, or status, refresh the list, and open a run on GitHub.
- Browse your codespaces with repository names, branches, last-used times, and status badges. Filter as you type, open one in your browser, or use **More** to copy its URL or name and refresh the list. Use **More > Create Codespace** from the Codespaces section or inside the Codespaces view to create one from a repository and optional branch, even when the list is empty.

Repository issue lists start with **Open** selected. Switch to **Closed** to see completed and not-planned issues. Text search narrows the selected state, and more results load as you scroll.

Repository pull request lists start with **Open** selected, including drafts. Switch to **Closed** to see closed and merged pull requests. Text search narrows the selected state, and more results load as you scroll.

Use **More > Merge pull request** on an open, non-draft PR to load a confirmation with the account, repository, target branch, expected head SHA, and enabled direct-merge methods. Confirming uses GitHub's async merge API, with repository rules enforced and bypass disabled. If the branch has a merge queue, GitHub enqueues the PR instead and the queue controls the merge method. The head SHA, branch, methods, and stack membership are checked again before submission.

**Pending** means processing, not merged. Use **Check status** to retrieve the async request's result without submitting another merge. **Enqueued** is not **Merged**: follow the queue on GitHub for the final outcome. **Failed** shows the failure returned by GitHub. After a timeout or unknown result, check GitHub before trying another merge. Cancelling or switching accounts only stops local work; it does not undo a submitted merge or remove a queue entry. Refresh the PR list to see its latest state.

Merging currently requires github.com and repository write access; fine-grained tokens need **Contents: write**. Unverified Enterprise Server hosts and unavailable APIs never fall back to the legacy `/merge` operation. Stack merges are deliberately blocked because the async API automatically includes every open downstack PR and provides no opt-out. Merge stacks on GitHub instead.

Actions lists start with **Running** selected, including queued runs; switch to **Succeeded** for successful runs or **Failed** for every other completed outcome, including cancelled and skipped runs. Each filter has a status icon, text search narrows the selected group, and more results load as you scroll.

Codespaces requires a github.com account and the `codespace` token scope. It isn't available on GitHub Enterprise Server. Use **More > Close Codespace** on an active codespace to stop it without deleting its files, or **More > Start Codespace** on a stopped one to start it. The list shows the state GitHub returns, and **Refresh** checks on a start or shutdown. Starting a codespace doesn't open it in your browser. Opening a stopped codespace still takes you to GitHub's browser editor, where it can start the environment.

### Agents access

Agents uses GitHub's [Agent Tasks API](https://docs.github.com/en/rest/agent-tasks/agent-tasks), which is in public preview. You need Copilot cloud agent access. Fine-grained tokens need **Agent tasks: read** on the repositories you want to see. OAuth user tokens are supported too.

Only non-archived tasks are listed. More tasks load as you scroll, and **Refresh** checks for new activity. GitHub Enterprise Server hosts without this API show an availability error instead of an empty list. Repository or model lookup failures stay visible in the task's subtitle, and you can still open the task.

Tasks still appear when the preview API leaves out their web links. The extension builds those links from the task IDs so you can open them on GitHub.

## Install

Releases aren't published yet. Until they are, build it yourself using the steps in [CONTRIBUTING.md](CONTRIBUTING.md).

## Signing in

Open Command Palette, type **GitHub**, and pick it.

**github.com:** Click **Sign in with GitHub**. Your browser opens, you approve the app, and you're back in Command Palette. The extension listens on `127.0.0.1` for a few minutes to catch GitHub's redirect, then stops.

**GitHub Enterprise Server:** Click **Sign in with GitHub Enterprise account**, enter your server URL (like `https://github.example.com`), and paste a [personal access token](https://docs.github.com/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens). Give the token the `repo`, `read:org`, `notifications`, and `codespace` scopes so future features have what they need.

To sign out, open the extension and pick **Sign out**.

## Troubleshooting

Failed GitHub REST calls write diagnostic messages to Command Palette's logs. Type **logs** in Command Palette to view them. Refresh notifications to retry a failed PR lookup; the preview now shows the API error instead of hiding it behind a generic message.

Log entries include the API host and path, HTTP status, GitHub request ID, rate-limit metadata, and whether GitHub sent an SSO header. Network failures and invalid JSON are logged too. Tokens, authorization headers, URL queries, and response bodies aren't logged. API paths can contain private repository names, so review logs before sharing them.

If an organization uses SAML single sign-on and hasn't authorized the extension yet, the pull request preview says so and links to GitHub's authorization page. You can also use **More > Authorize single sign-on**. Approve it, then refresh notifications. If your org restricts OAuth apps, an org owner may need to approve the app first.

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
