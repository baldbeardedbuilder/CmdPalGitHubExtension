---
title: Repositories
description: Find repositories you can access and open their issues, pull requests, workflows, and agent tasks.
---

Open **GitHub > Repos**. You'll start with your personal repositories. Type to filter those loaded rows, then search repositories you can access on your signed-in host.

Use **Load more** when another search page is available. Each click adds one page. GitHub search exposes at most 1,000 results for a query, so narrow the query if you reach that limit.

## Open a repository

Select a repository to open its menu:

| Section | Destination |
| --- | --- |
| Repository name | The repository on GitHub. |
| **Issues** | The repository's issue list. |
| **Pull Requests** | The repository's pull request list. |
| **Actions** | Workflow runs for that repository. |
| **Start Copilot task** | A task form for that repository. |
| **Discussions** | The repository's discussions on GitHub. |

Repository rows also offer **Copy clone URL** when a clone URL is available, and **Copy name**. Opening a repository doesn't clone it to your machine.

## Star repositories

Open **Starred repositories** from the home page to browse your starred repositories. The list loads page by page; use **Load more** to continue. A repository's **Manage star** action checks your personal star state and offers a confirmed star or unstar action. It doesn't derive your state from the public star count.

Your token needs permission to read and write your starred repositories. Organization policies may also restrict access.

## Watch or ignore a repository

Choose **Manage watching** in a repository row's **More** menu. The extension reads your actual subscription and distinguishes **Watching**, **Not watching**, and **Ignored**. It doesn't infer your subscription from a public watcher count.

**Watch repository**, **Unwatch repository**, and **Ignore repository** each require confirmation. Unwatch removes your subscription and clears an ignored state. Before writing, the extension checks that the subscription still matches the state you reviewed. If someone changed it, refresh and review again.

These actions don't configure custom notification categories. Use GitHub's Watch menu when you need those settings. Token support and repository access matter; denied access isn't shown as “not watching.”

Use OAuth sign-in or a classic PAT with the scopes your repository needs. GitHub doesn't advertise fine-grained PAT or GitHub App token support for these subscription endpoints. The extension doesn't use deprecated watched-repository listing endpoints.

## Find a missing repo

Check the signed-in account and host first. For a private repository, your account and token both need access. Organization policies and single sign-on can further restrict results.

Read [filtering and search](/CmdPalGitHubExtension/reference/filtering/) if you're unsure which results you're searching.
