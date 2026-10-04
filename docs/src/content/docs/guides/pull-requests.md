---
title: Pull requests
description: Browse repository pull requests and understand the scope of a merge confirmation.
---

Open **GitHub > Repos**, choose a repository, and select **Pull Requests**. Select a row to open it in your browser, or use **Copy link** from **More**.

Choose **Open** or **Closed** and type to filter loaded rows. Drafts count as open; merged pull requests count as closed. **Load more** fetches another page and keeps your filters.

## Update a pull request

Use **Manage pull request** in a row's **More** menu to close or reopen an unmerged pull request, request or remove reviewers, manage assignees, or add and remove existing labels. Each change shows a confirmation and checks the current pull request before sending. Merged pull requests cannot be closed or reopened through these actions.

The same actions are available from pull request notifications after their details load. If the details aren't available, open the pull request on GitHub.

## Review pull request details and conversation

**Open in browser** is a row's primary command, and **Show details** is secondary. Details render the description as Markdown and show changed files, submitted reviews, check runs, and combined commit status. **Refresh** is the detail page's primary command, not a button in the description. Each section reports its own load failure; unavailable check data is never treated as a passing result.

Choose **Conversation** to browse, post, edit, or delete comments using the same author and maintainer rules as issue comments. Use **Post comment** for a new comment and **Save comment** when editing. The conversation icon follows the pull request's state. Pinning to Command Palette home or dock is disabled until the extension supports reopening pinned destinations.

When contextual Codespaces are wired in, the repository row's **More** menu includes **Open Codespace** for the pull request's head branch.

From native details, authorized repository writers can convert an open pull request to draft or mark it ready for review. These actions use GitHub's GraphQL mutations and verify the pull request afterward. A GitHub host that does not support the required GraphQL fields will report the limitation rather than silently falling back.

The details page can also update the pull request branch from its base branch. The confirmation pins the inspected head SHA, and GitHub merges the base into the pull request branch; it does not rebase. GitHub may accept the operation asynchronously, so refresh to verify the result.

To review another author's open pull request, prepare an approval, comment, or request-changes review and confirm it. The extension creates a pending review pinned to the inspected commit before submitting it. If submission is uncertain, refresh and inspect reviews before retrying; it will not create another pending draft automatically.

## Merge an eligible pull request

An open, non-draft pull request can offer **Merge pull request** in **More**. The extension checks the current target and your repository permissions before showing a confirmation.

Read that confirmation carefully. It includes the account, host, target branch, expected head commit, available direct-merge methods, and current stack metadata.

:::caution[The merge scope can be larger than one pull request]
For a stacked pull request, the async API can merge **all open downstack pull requests** too. The API pins this pull request's head commit, not its target branch or exact downstack set. Those can change after the final check.
:::

Choose a direct-merge method and explicitly accept that scope before selecting **Confirm merge or enqueue**. If the repository uses a merge queue, the queue controls the method. Repository rules still apply; the extension doesn't bypass them.

Don't confirm if you need a fixed target branch or an exact downstack set. Review and merge on GitHub instead.

## Check the result

GitHub accepting the request doesn't mean the pull request merged. Use **Check status** or open it on GitHub to verify the result. **Cancel / stop checking** stops local checking, not a merge already submitted to GitHub.

Async merge support isn't verified for Enterprise Server hosts. The extension directs you to GitHub rather than attempting a legacy fallback.

See [confirmations and pending requests](/CmdPalGitHubExtension/reference/request-safety/) before resubmitting an uncertain merge.
