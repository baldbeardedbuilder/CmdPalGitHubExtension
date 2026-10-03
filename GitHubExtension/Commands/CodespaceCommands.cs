// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class RefreshCodespacesCommand : InvokableCommand
{
    private readonly CodespacesPage _page;

    public RefreshCodespacesCommand(CodespacesPage page)
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

internal sealed partial class CloseCodespaceCommand : InvokableCommand
{
    private readonly CodespacesPage _page;
    private readonly CodespaceItem _item;

    public CloseCodespaceCommand(CodespacesPage page, CodespaceItem item)
    {
        _page = page;
        _item = item;
        Name = "Close Codespace";
        Icon = Icons.Stop;
    }

    public override ICommandResult Invoke()
    {
        _ = _page.CloseAsync(_item);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class StartCodespaceCommand : InvokableCommand
{
    private readonly CodespacesPage _page;
    private readonly CodespaceItem _item;
    private readonly bool _confirmed;

    public StartCodespaceCommand(CodespacesPage page, CodespaceItem item)
        : this(page, item, false)
    {
    }

    private StartCodespaceCommand(CodespacesPage page, CodespaceItem item, bool confirmed)
    {
        _page = page;
        _item = item;
        _confirmed = confirmed;
        Name = "Start Codespace";
        Icon = Icons.Start;
    }

    public override ICommandResult Invoke()
    {
        if (!_confirmed)
        {
            return CommandResult.Confirm(new ConfirmationArgs
            {
                Title = "Start Codespace?",
                Description = $"Starting {_item.Codespace.Name} uses compute time and may incur charges. Continue?",
                PrimaryCommand = new StartCodespaceCommand(_page, _item, true),
                IsPrimaryCommandCritical = true,
            });
        }

        _ = _page.StartAsync(_item);
        return CommandResult.KeepOpen();
    }
}
