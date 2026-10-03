// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class RefreshActionsCommand : InvokableCommand
{
    private readonly ActionsPage _page;

    public RefreshActionsCommand(ActionsPage page)
    {
        _page = page;
        Name = "Refresh";
        Icon = Icons.Refresh;
    }

    public override ICommandResult Invoke()
    {
        _ = _page.RefreshAsync();
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class CancelWorkflowRunCommand : InvokableCommand
{
    private readonly ActionsPage _page;
    private readonly WorkflowRunItem _item;

    public CancelWorkflowRunCommand(ActionsPage page, WorkflowRunItem item)
    {
        _page = page;
        _item = item;
        Name = "Cancel";
        Icon = Icons.Stop;
    }

    public override ICommandResult Invoke()
    {
        _ = _page.CancelAsync(_item);
        return CommandResult.KeepOpen();
    }
}
