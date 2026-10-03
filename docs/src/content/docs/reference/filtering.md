---
title: Filtering and search
description: Know when you're filtering loaded rows and when you're searching GitHub.
---

Typing doesn't mean the same thing on every page. Most lists filter what you've already loaded; **Repos** also sends a repository search to GitHub.

| Page | What typing searches |
| --- | --- |
| **Repos** | Loaded personal repositories first, then accessible repositories on your signed-in host. |
| **Issues** | Loaded issue rows, combined with the Open or Closed filter. |
| **Pull Requests** | Loaded pull request rows, combined with the Open or Closed filter. |
| **Notifications** | Loaded notifications and available subject state. |
| **Agents** | Loaded tasks, including title, repository, model, and state. |
| **Codespaces** | Loaded environments, including repository, name, branch, and state. |
| **Actions** | Loaded workflow runs and the selected status filter. |
| **Search issues and pull requests** | A GitHub search across accessible repositories, using your qualifiers and selected result type. |

## Load another page

In repository search, Issues, and Pull Requests, **Load more** fetches one additional page when available. Your existing filters remain in place.

If a filter matches nothing and more pages exist, keep loading or open GitHub for a broader search. An empty filtered list isn't proof that no matching result exists.

GitHub repository search exposes at most **1,000 results per query**. Use a narrower query to reach repositories outside that window.

## Pick the right host

Search uses the account and GitHub host you signed in to. It doesn't combine github.com and Enterprise results. [Sign out and sign back in](/CmdPalGitHubExtension/getting-started/sign-in/#sign-out-or-change-accounts) to switch.

## Search work across repositories

Open **Search issues and pull requests** to find assigned issues, work you've authored, or PRs awaiting your review. Presets use GitHub's `author`, `assignee`, and `review-requested` qualifiers. You can enter other supported GitHub qualifiers, including `repo:owner/name`, `org:`, `label:`, and `is:open`.

Choose **Run or save a search query** to select issues, pull requests, or both. A PR result stays labeled as a PR; it isn't treated as an issue simply because both use the search API. Selecting a result opens its existing GitHub destination, with native description details available alongside it.

The result's **More** menu can open native pull request details or an issue/PR conversation when those features are enabled. Starting another query invalidates pages and actions from the earlier results.

Search loads one page at a time and stops at GitHub's **1,000-result limit**. The result footer shows the count and calls out incomplete responses. Narrow your query rather than assuming every match is loaded. Rate-limit errors leave existing results visible.

## Save a query on this device

In **Run or save a search query**, choose **Save query**, enter a name, and supply the query and result type. Saved queries appear next to the presets when the search is empty. Saving the same name replaces it. Choose **Delete saved query** and enter its name to remove it.

Queries are extension state, not a GitHub saved-search endpoint. They live under your local app data directory and are separated by signed-in account and host. Tokens aren't written into saved-query files. Your query text is stored, so don't put secrets in it.

Notification and agent API filters are separate from typing. Changing an API scope clears results and resets pagination. Their search boxes still filter only the rows loaded under that scope.
