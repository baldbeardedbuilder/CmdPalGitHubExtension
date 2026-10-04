---
title: Issues
description: Browse and filter the issues loaded for a repository.
---

Open **GitHub > Repos**, choose a repository, then select **Issues**.

Rows show the issue number, title, age, and comment count. Select a row to open issue details in Command Palette, or use **Open in browser** or **Copy link** from its **More** menu.

## Create, edit, and discuss issues

Choose **Create an issue** at the top of a repository's issue list to write a title and description and choose an open milestone. Select **Save** to review the result, then confirm to create the issue. **Cancel** returns to the issue list without saving. Open an issue's **More** menu to edit those fields and its milestone. Removing a milestone is supported; an existing closed milestone remains available while editing. The editor checks that the issue text and milestone have not changed since the row was loaded before saving.

Choose **Conversation** from an issue's **More** menu to read paged comments, post a comment, edit your own comments, or delete a comment you authored or can manage as a repository maintainer. If GitHub accepts a comment but the follow-up refresh fails, the editor clears the submitted draft and asks you to refresh instead of risking a duplicate.

Issue templates and forms are not applied in this editor, and fields beyond title, description, and milestone are not supported. Use GitHub when the repository requires a template or structured issue form.

## Update issue state and metadata

Issue descriptions render as Markdown. **Open in browser** is the primary command, and **Refresh** is secondary. State and metadata actions live in **More**, not in the description.

Use **Close** to choose **Close as completed** or **Close as not planned**. Closed issues offer **Reopen issue**. You can assign or remove yourself, use **Assign** or **Remove assignee** for other people, and choose **Add label** or **Remove label** under **Add/remove labels**. Each change has a confirmation step and is checked against the current issue state. These actions don't create labels or edit issue text.

When you open an issue from a notification, changing its state or metadata refreshes the notification preview from GitHub. **Mark as done** remains a separate notification action.

## Filter and load more

Choose **Open** or **Closed**, then type to filter the rows you've loaded. When there are more pages, use **Load more** to fetch the next one without losing your filters.

:::note[Loaded results only]
A blank list doesn't mean the repository has no matching issues. Your match may be on a page you haven't loaded yet. The page shows the loaded-results scope when more results are available.
:::

## When to open GitHub

Use the browser to create labels or work with unsupported issue fields. The extension's issue actions cover common workflows, not every issue workflow. Pinning to Command Palette home or dock is disabled until the extension supports reopening pinned destinations.

See [filtering and search](/CmdPalGitHubExtension/reference/filtering/) for the distinction between local filtering and repository search.
