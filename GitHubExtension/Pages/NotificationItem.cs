// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// One row on the notifications page. It updates itself in place as we learn more (state, read status).
/// </summary>
internal sealed partial class NotificationItem : ListItem
{
    private readonly NotificationsPage _page;
    private readonly IBrowserLauncher _browser;
    private SubjectDetails? _subject;

    public NotificationItem(NotificationsPage page, GitHubNotification notification, Uri webUrl, IBrowserLauncher browser, DateTimeOffset now)
    {
        _page = page;
        _browser = browser;
        Notification = notification;
        Unread = notification.Unread;
        WebUrl = webUrl;
        Command = new OpenNotificationCommand(page, this);
        Title = notification.Title;
        Subtitle = $"{notification.RepositoryFullName} \u00B7 {NotificationFormatting.RelativeTime(notification.UpdatedAt, now)}";
        Refresh();
    }

    public GitHubNotification Notification { get; }

    public bool Unread { get; private set; }

    public Uri WebUrl { get; private set; }

    public SubjectDetails? Subject => _subject;

    public string SearchText => string.Join(
        ' ',
        Notification.Title,
        Notification.RepositoryFullName,
        Notification.SubjectType,
        Notification.Reason,
        _subject?.State.ToString() ?? string.Empty,
        Unread ? "unread" : string.Empty);

    public void SetUnread(bool unread)
    {
        if (Unread != unread)
        {
            Unread = unread;
            Refresh();
        }
    }

    public void ApplySubject(SubjectDetails subject)
    {
        _subject = subject;
        if (subject.WebUrl is { } url)
        {
            WebUrl = url;
        }

        Refresh();
    }

    private void Refresh()
    {
        Icon = Icons.NotificationIcon(NotificationFormatting.Glyph(Notification.SubjectType), Unread);
        Tags = NotificationFormatting.StateTag(Notification.SubjectType, _subject?.State ?? SubjectState.Unknown) is { } tag ? [tag] : [];

        var more = new List<IContextItem>();
        more.Add(new CommandContextItem(new OpenInBrowserCommand(
            _browser,
            WebUrl,
            "Open in browser",
            Icons.NotificationIcon(NotificationFormatting.Glyph(Notification.SubjectType), unread: false))));
        if (Unread)
        {
            more.Add(new CommandContextItem(new MarkNotificationReadCommand(_page, this)));
        }

        more.Add(new CommandContextItem(new MarkNotificationDoneCommand(_page, this)));
        if (Notification.RepositoryWebUrl is { } repo)
        {
            more.Add(new CommandContextItem(new OpenUrlCommand(repo.AbsoluteUri) { Name = "Open repository", Icon = Icons.Repos }));
        }

        more.Add(new CommandContextItem(new CopyTextCommand(WebUrl.AbsoluteUri) { Name = "Copy link", Icon = Icons.Copy }));
        more.Add(new CommandContextItem(new RefreshNotificationsCommand(_page)));
        MoreCommands = [.. more];
    }
}
