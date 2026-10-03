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

## Manage a thread

When thread details are available, **More** includes actions to subscribe, unsubscribe, or ignore the conversation. **Unsubscribe** removes your explicit thread subscription and returns to repository notification rules. A watched repository can still notify you. **Ignore** suppresses future notifications from that thread until you comment or are mentioned.

Subscription actions do not mark a notification read or done. Issue notifications also support the issue actions in [Issues](/CmdPalGitHubExtension/guides/issues/), and pull request notifications offer the actions in [Pull requests](/CmdPalGitHubExtension/guides/pull-requests/).

When native work details are enabled, **More** also opens pull request details or the issue/PR conversation inside Command Palette. Browser actions stay available. Changing the account or API scope invalidates those earlier detail pages and confirmations.

## Filter your inbox

Type to filter loaded notifications by title, repository, type, reason, state, or unread status. This isn't a new search across GitHub.

Choose **Filter notification API results** in **More** to fetch unread-only or participating notifications, optionally scoped to one `owner/name` repository and a since/before time window. Enter timestamps with `Z` or an explicit timezone offset. API query changes reset pagination; your local search text stays in place.

Refresh uses GitHub's last-modified validator. A `304 Not Modified` response keeps the current rows and pagination. The extension waits at least GitHub's `X-Poll-Interval` between first-page refreshes (60 seconds when GitHub doesn't return a valid interval). This is conditional refresh, not background polling. Loading another page is a separate request.

If an issue or PR preview failed, **Refresh** can retry that preview during the inbox's waiting period. It doesn't send another notification-list request early. A `304` also leaves failed previews eligible for a retry.

GitHub currently documents these endpoints for classic PATs with `notifications` or `repo` scope. Existing OAuth sign-in sends its scoped token, but if GitHub denies it, check that authorization or use a classic PAT. These notification endpoints don't support fine-grained PATs or GitHub App tokens. See [GitHub's notification API documentation](https://docs.github.com/en/rest/activity/notifications).

## Mark a scope read

Choose **Mark notifications read in bulk** from **More**. Review the cutoff timestamp, then choose all repositories or a specific repository. **Change cutoff timestamp** also lets you enter a repository that isn't loaded in the list.

The confirmation shows your account, host, exact scope, and UTC cutoff. The action marks notifications updated at or before that cutoff read across the entire chosen scope. It isn't limited to the rows your local search or API filters show.

Cancel sends nothing. If GitHub returns an asynchronous response, the result says the request is still processing and requests an inbox refresh. That refresh waits for the current polling interval instead of immediately sending another GET. Check the outcome before retrying. Switching accounts invalidates an earlier confirmation.

If details can't be loaded, you can still open the notification on GitHub. An **Authorize single sign-on** action may appear when your organization's authorization is missing.

If a change fails or its result is uncertain, keep the error in view and [check the request outcome](/CmdPalGitHubExtension/reference/request-safety/) before trying again.
