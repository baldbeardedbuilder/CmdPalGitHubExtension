// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class OpenNotificationCommand : InvokableCommand
{
    private readonly NotificationsPage _page;
    private readonly NotificationItem _item;

    public OpenNotificationCommand(NotificationsPage page, NotificationItem item)
    {
        _page = page;
        _item = item;
        Name = "Open";
    }

    public override ICommandResult Invoke()
    {
        return _page.Open(_item);
    }
}

internal sealed partial class MarkNotificationReadCommand : InvokableCommand
{
    private readonly NotificationsPage _page;
    private readonly NotificationItem _item;

    public MarkNotificationReadCommand(NotificationsPage page, NotificationItem item)
    {
        _page = page;
        _item = item;
        Name = "Mark as read";
        Icon = Icons.MarkRead;
    }

    public override ICommandResult Invoke()
    {
        _page.MarkAsRead(_item);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class MarkNotificationDoneCommand : InvokableCommand
{
    private readonly NotificationsPage _page;
    private readonly NotificationItem _item;

    public MarkNotificationDoneCommand(NotificationsPage page, NotificationItem item)
    {
        _page = page;
        _item = item;
        Name = "Mark as done";
        Icon = Icons.Done;
    }

    public override ICommandResult Invoke()
    {
        _page.MarkAsDone(_item);
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class RefreshNotificationsCommand : InvokableCommand
{
    private readonly NotificationsPage _page;

    public RefreshNotificationsCommand(NotificationsPage page)
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
