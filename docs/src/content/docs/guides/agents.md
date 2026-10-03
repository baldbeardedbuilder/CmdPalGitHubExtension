---
title: Copilot agent tasks
description: Follow your Copilot cloud agent tasks or review a new task before submitting it.
---

Open **GitHub > Agents** to see your Copilot agent tasks. Rows include status and available repository/model details. Type to filter loaded tasks by title, repository, model, or state.

Select a task to open it on GitHub. **More** includes **Copy URL** and **Refresh**.

## Read task details

Choose **Task details** from a task's **More** menu without leaving Command Palette. You can inspect each returned session's prompt, state, model, timestamps, branches, and error message. Usage keeps GitHub's returned units, such as `ai_credits` or `premium_requests`. It isn't a dollar estimate.

Branch artifacts open the generated branch. Pull request artifacts open the exact PR when GitHub returns a resolvable global node ID. If it doesn't, the action opens the repository's pull request list and labels the artifact ID clearly. That ID isn't a PR number. Unknown artifact types remain visible without an invented link.

This view isn't a live log stream. Open the task on GitHub when you need more context.

## Browse task history

Use **Filter agent API results** in **More** to choose non-archived or archived tasks, a task state, and an optional `owner/name` repository scope. Changing the API query clears the previous results and starts pagination again. Typing still filters only the rows you've loaded.

Repository rows also offer repository-scoped agents in **More**. Archived history is read-only. There isn't an archive or unarchive action here.

The Agent Tasks API is in public preview and may not be available for every account or Enterprise host. GitHub App installation tokens aren't supported; supported fine-grained PATs need **Agent tasks: read** access.

## Start a task

1. Open **GitHub > Repos** and choose a repository.
2. Select **Start Copilot task**.
3. Enter a **Prompt** that describes the work.
4. Optionally choose a **Model**, **Custom agent**, **Base branch**, or **Head branch**, and whether to **Create a pull request**.
5. Select **Review task**, check the repository and options, then select **Start task**.

Model and custom agent availability depends on your plan and organization policy. For a custom agent, enter its filename without the extension. Leave optional fields blank to omit them from the request.

Starting a task uses Copilot cloud agent compute and may consume premium requests or AI credits.

## Access requirements

Task creation through this API requires Copilot Business or Enterprise and **Agent tasks: read and write** access on the repository. Viewing tasks requires the relevant Copilot access and **Agent tasks: read** permission for fine-grained tokens. Organization policies may still prevent an action.

If GitHub denies the request, check those requirements before changing the prompt or trying again.

## If the response is lost

**Task outcome needs checking** means the task may already exist. Use **Check again** or **Open repository tasks**, and inspect any new task candidates before submitting another request.

**Edit draft** doesn't cancel a task that GitHub accepted. If you proceed to **Start another task**, the earlier request may still be running and consuming compute.
