---
title: Notifications
description: Preview your GitHub inbox, open notifications, and mark work read or done.
---

Open **GitHub > Notifications** to catch up on your inbox. Rows show the repository, update time, and notification type. Issue and pull request details load when GitHub makes them available.

## Open or clear a notification

Select a notification to open it in your browser. Opening an unread notification also requests that GitHub mark it read.

The row's **More** menu includes:

| Action | What it does |
| --- | --- |
| **Open in browser** | Opens the notification's destination on GitHub. |
| **Mark as read** | Marks an unread notification read without opening it. |
| **Mark as done** | Requests that GitHub clear it from your inbox. |
| **Open repository** | Opens its repository, when a repository link is available. |
| **Copy link** | Copies the notification destination. |
| **Refresh** | Reloads the inbox from GitHub. |

The extension refreshes the inbox after notification changes instead of assuming a write succeeded. Repeated clicks don't send duplicate writes for the same notification while its request is active.

## Filter your inbox

Type to filter loaded notifications by title, repository, type, reason, state, or unread status. This isn't a new search across GitHub.

If details can't be loaded, you can still open the notification on GitHub. An **Authorize single sign-on** action may appear when your organization's authorization is missing.

If a change fails or its result is uncertain, keep the error in view and [check the request outcome](/CmdPalGitHubExtension/reference/request-safety/) before trying again.
