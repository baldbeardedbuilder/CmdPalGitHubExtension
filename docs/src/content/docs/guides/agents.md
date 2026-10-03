---
title: Copilot agent tasks
description: Follow your Copilot cloud agent tasks or review a new task before submitting it.
---

Open **GitHub > Agents** to see your Copilot agent tasks. Rows include status and available repository/model details. Type to filter loaded tasks by title, repository, model, or state.

Select a task to open it on GitHub. **More** includes **Copy URL** and **Refresh**.

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
