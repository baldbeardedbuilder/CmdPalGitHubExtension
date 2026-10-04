// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class IssueDetailsPage
{
    private void PublishItemsChanged()
    {
        IssueDetailsForm form;
        GitHubIssue? issue;
        string? login;
        bool enabled;
        bool canRefresh;
        lock (_lock)
        {
            form = _form;
            issue = _issue;
            login = _loadedAccount?.Login;
            enabled = !_load.Disposed && !_load.Fetching && ReferenceEquals(_loadedAccount, _auth.CurrentAccount);
            canRefresh = enabled && _loadedAccount is not null && _issueApiUrl is not null;
        }

        var commands = new List<IContextItem>();
        if (enabled && form.ShowsDescription && issue is not null)
        {
            commands.Add(Action("Open in browser", IssueDetailsActions.OpenInBrowser));
        }
        if (canRefresh)
        {
            commands.Add(Action("Refresh", IssueDetailsActions.Retry));
        }
        if (enabled && form.ShowsDescription && issue is not null && _mutations is not null)
        {
            if (issue.State == SubjectState.Open)
            {
                commands.Add(Group("Close",
                    Action("Close as completed", IssueDetailsActions.CloseCompleted),
                    Action("Close as not planned", IssueDetailsActions.CloseNotPlanned)));
            }
            else if (issue.State is SubjectState.Closed or SubjectState.NotPlanned)
            {
                commands.Add(Action("Reopen issue", IssueDetailsActions.Reopen));
            }
            if (!string.IsNullOrWhiteSpace(login))
            {
                var assigned = issue.Assignees.Contains(login, StringComparer.OrdinalIgnoreCase);
                commands.Add(Action(assigned ? "Remove myself" : "Assign myself",
                    assigned ? IssueDetailsActions.RemoveSelf : IssueDetailsActions.AssignSelf));
            }
            commands.Add(Action("Assign", IssueDetailsActions.AddAssignee));
            if (issue.Assignees.Count > 0)
            {
                commands.Add(Action("Remove assignee", IssueDetailsActions.RemoveAssignee));
            }
            commands.Add(Group("Add/remove labels", issue.Labels.Count > 0
                ? [Action("Add label", IssueDetailsActions.AddLabel), Action("Remove label", IssueDetailsActions.RemoveLabel)]
                : [Action("Add label", IssueDetailsActions.AddLabel)]));
        }

        Commands = [.. commands];
        Icon = issue is null ? Icons.Issues : Icons.SubjectIcon(false, issue.State);
        RaiseItemsChanged();

        CommandContextItem Action(string name, string action) =>
            new(new IssueDetailCommand(this, form, action) { Name = name, Icon = action == IssueDetailsActions.Retry ? Icons.Refresh : Icons.Issues });
    }

    private static CommandContextItem Group(string name, params IContextItem[] children) =>
        new(new NoOpCommand { Name = name }) { MoreCommands = children };

    private sealed partial class IssueDetailCommand(IssueDetailsPage page, IssueDetailsForm source, string action) : InvokableCommand
    {
        public override ICommandResult Invoke() => page.Submit(source, "{}", action);
    }
}
