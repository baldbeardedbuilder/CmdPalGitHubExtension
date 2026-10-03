---
title: GitHub Actions
description: Browse repository workflow runs, cancel an active run, or review a rerun.
---

Open **GitHub > Repos**, choose a repository, then select **Actions**. Rows show workflow run status and available actor, timing, and attempt details.

Type to filter loaded runs. Status filters include **Running**, **Succeeded**, and **Failed**. Select a run to open its full details and logs on GitHub. **More** includes **Copy run URL** and **Refresh**.

## Cancel an active run

Use **More > Cancel** on an active workflow run. Cancellation requires Actions write permission.

GitHub can accept the cancellation before the run actually stops. The extension refreshes its status until GitHub reports a terminal state.

If normal cancellation is stuck, use **More > Force cancel**. That opens a separate confirmation. Check the target before confirming; force cancellation is not the first step for every run.

## Rerun a completed workflow

Eligible completed runs offer **More > Rerun workflow...**.

1. Review the repository, workflow, run ID, and attempt.
2. Choose **All jobs**, or **Failed jobs and their dependents** when available.
3. Decide whether to **Enable debug logging**.
4. Select **Confirm rerun**.

Reruns use GitHub Actions compute and may incur charges. Repository and token permissions still apply.

Use **Refresh status** to check the new attempt and status. An accepted request isn't a completed rerun. If the outcome is uncertain, check GitHub before sending another request.
