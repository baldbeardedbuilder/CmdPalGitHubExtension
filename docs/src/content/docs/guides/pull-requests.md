---
title: Pull requests
description: Browse repository pull requests and understand the scope of a merge confirmation.
---

Open **GitHub > Repos**, choose a repository, and select **Pull Requests**. Select a row to open it in your browser, or use **Copy link** from **More**.

Choose **Open** or **Closed** and type to filter loaded rows. Drafts count as open; merged pull requests count as closed. **Load more** fetches another page and keeps your filters.

## Update a pull request

Use **Manage pull request** in a row's **More** menu to close or reopen an unmerged pull request, request or remove reviewers, manage assignees, or add and remove existing labels. Each change shows a confirmation and checks the current pull request before sending. Merged pull requests cannot be closed or reopened through these actions.

The same actions are available from pull request notifications after their details load. If the details aren't available, open the pull request on GitHub.

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
