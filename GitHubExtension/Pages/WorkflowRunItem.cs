// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class WorkflowRunItem : ListItem
{
    public WorkflowRunItem(ActionsPage page, string repository, GitHubWorkflowRun run, IBrowserLauncher browser, DateTimeOffset now)
    {
        Run = run;
        Title = run.Name;
        Subtitle = WorkflowRunFormatting.Subtitle(run, now);
        Icon = WorkflowRunFormatting.Icon(run);
        Details = new WorkflowRunDetails(repository, run);
        var state = WorkflowRunFormatting.State(run);
        SearchText = $"{run.Name} {run.DisplayTitle} {run.Actor} {run.Status} {run.Conclusion} {state}";
        Command = new OpenInBrowserCommand(browser, run.WebUrl, "Open", Icons.Actions);
        MoreCommands =
        [
            new CommandContextItem(new NoOpCommand()) { Title = $"Status: {state}", Icon = Icon },
            new CommandContextItem(new CopyTextCommand(run.WebUrl.AbsoluteUri) { Name = "Copy run URL", Icon = Icons.Copy }),
            new CommandContextItem(new RefreshActionsCommand(page)),
        ];
        if (run.CanRerun)
        {
            MoreCommands = [.. MoreCommands, new CommandContextItem(page.RerunPage(repository, run))];
        }
    }

    public GitHubWorkflowRun Run { get; }

    public string SearchText { get; }
}
