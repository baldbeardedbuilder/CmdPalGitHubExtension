---
title: GitHub Actions
description: Browse workflow runs, inspect jobs, download artifacts, and run workflows.
---

Open **GitHub > Repos**, choose a repository, then select **Actions**. Rows show workflow run status and available actor, timing, and attempt details.

Type to filter loaded runs. Status filters include **Running**, **Succeeded**, and **Failed**. Select a run to open its full details and logs on GitHub. **More** includes **Copy run URL** and **Refresh**.

## Run a workflow

When a repository has no visible runs, select the empty state to open **Run workflow manually**. Choose an active workflow and an existing branch. The extension reads the workflow file at that ref and only enables dispatch when it can validate the `workflow_dispatch` inputs it finds.

Input types supported here are strings, numbers, booleans, choices, and environments. Choice values and defaults come from the workflow file. Unsupported YAML or workflows without `workflow_dispatch` stay on the page with an explanation instead of sending an unverified request.

Review the repository, workflow, ref, every input value, and which values came from defaults before confirming. Dispatch starts GitHub Actions compute and may incur charges. A successful API response means GitHub accepted the request, not that a run has appeared or completed.

## Inspect jobs

Choose **More > View jobs and steps** on a run. The list is paged; selecting a job refreshes its current status and step results from GitHub. Failed step names are shown in the job summary.

A completed job can be rerun after you review its current result and confirm. Rerunning one job also reruns jobs that depend on it, and uses Actions compute that may incur charges. The extension checks the current job state and repository write access again before sending the request.

## Download logs and artifacts

Choose **More > Download logs and artifacts** on a run. Workflow logs are available as a ZIP, and artifacts appear in a paged list. Expired artifacts remain visible but can't be downloaded.

Enter a destination path in an existing folder. Choose **Cancel download** to stop an in-progress transfer. The file is written to a temporary sibling and replaces the destination only after the download completes, so canceling doesn't leave a partial file or replace an existing one. GitHub's signed download links can expire; request a fresh download from the run when that happens. The GitHub token is used only with the GitHub API and is never sent to the redirected download host.

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
