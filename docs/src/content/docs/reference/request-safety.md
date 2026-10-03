---
title: Confirmations and pending requests
description: Understand confirmation screens, accepted requests, and uncertain outcomes before retrying an action.
---

Actions that start compute, create resources, rerun workflows or jobs, submit agent work, merge pull requests, or delete Codespaces need a review or confirmation. Read the target and warnings before submitting.

## Accepted isn't finished

GitHub often accepts a request and completes it later. A Codespace may still be preparing, a cancellation may still be stopping jobs, a branch update may still be merging the base, and a merge may be queued.

Refresh or use the page's status action to check the authoritative state. Don't treat an accepted request as proof that the requested change finished.

## A timeout isn't a rejection

If the response is lost after submission, GitHub may still have received the request. Submitting again can create a second Codespace, agent task, or workflow attempt.

Use the page's status/check actions and inspect GitHub before retrying. The extension deduplicates active submissions and keeps uncertain results visible. Codespace creation stays blocked when it can't prove another creation is safe.

Agent task pages can show candidate tasks and offer an explicit **Start another task** path after editing. That doesn't prove the previous task failed. Inspect those candidates first.

## Account changes don't roll back GitHub

Signing out or changing accounts cancels local work and discards stale results. It doesn't undo writes that GitHub already accepted.

Likewise, stopping a merge status check doesn't cancel the submitted merge. Check the result on GitHub using the account that submitted it.

## When not to use the extension

Use GitHub directly when you need inline diff editing, a live workflow log viewer, or a merge with a fixed branch and exact downstack scope. A confirmation is useful, but it can't pin everything that GitHub's asynchronous APIs may change.
