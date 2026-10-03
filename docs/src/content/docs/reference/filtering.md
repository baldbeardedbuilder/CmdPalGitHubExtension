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

## Load another page

In repository search, Issues, and Pull Requests, **Load more** fetches one additional page when available. Your existing filters remain in place.

If a filter matches nothing and more pages exist, keep loading or open GitHub for a broader search. An empty filtered list isn't proof that no matching result exists.

GitHub repository search exposes at most **1,000 results per query**. Use a narrower query to reach repositories outside that window.

## Pick the right host

Search uses the account and GitHub host you signed in to. It doesn't combine github.com and Enterprise results. [Sign out and sign back in](/CmdPalGitHubExtension/getting-started/sign-in/#sign-out-or-change-accounts) to switch.

**Saved Queries** is currently a coming-soon placeholder. You can't save or run queries from it yet.
