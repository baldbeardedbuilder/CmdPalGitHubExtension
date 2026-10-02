// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class OpenActionsCommand : InvokableCommand
{
    private readonly ActionsPage _page;
    private readonly string _repository;

    public OpenActionsCommand(ActionsPage page, string repository)
    {
        _page = page;
        _repository = repository;
        Name = "Actions";
        Icon = Icons.Actions;
    }

    public override ICommandResult Invoke() => _page.OpenRepository(_repository);
}

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
