// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class AgentItem : ListItem
{
    public AgentItem(AgentsPage page, GitHubAgentTask task, IBrowserLauncher browser, DateTimeOffset now)
    {
        Task = task;
        Title = task.Title;
        Subtitle = AgentFormatting.Subtitle(task, now);
        Icon = Icons.Agents;
        Tags = [AgentFormatting.StateTag(task.State)];
        Command = new OpenInBrowserCommand(browser, task.WebUrl, "Open", Icons.Agents);
        MoreCommands =
        [
            new CommandContextItem(new CopyTextCommand(task.WebUrl.AbsoluteUri) { Name = "Copy URL", Icon = Icons.Copy }),
            new CommandContextItem(new RefreshAgentsCommand(page)),
        ];
    }

    public GitHubAgentTask Task { get; }

    public bool Matches(string[] terms) => terms.All(t =>
        Task.Title.Contains(t, StringComparison.OrdinalIgnoreCase)
        || (Task.RepositoryFullName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Task.Model?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
        || Task.State.Contains(t, StringComparison.OrdinalIgnoreCase)
        || AgentFormatting.StateText(Task.State).Contains(t, StringComparison.OrdinalIgnoreCase));
}
