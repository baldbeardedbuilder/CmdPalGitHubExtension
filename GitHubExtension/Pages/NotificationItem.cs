// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;

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
        Account = page.CurrentAccount;
        AccountGeneration = page.AccountGeneration;
        _browser = browser;
        Notification = notification;
        Unread = notification.Unread;
        WebUrl = webUrl;
        Command = new OpenNotificationCommand(page, this);
        Title = notification.Title;
        Subtitle = $"{notification.RepositoryFullName} \u00B7 {NotificationFormatting.RelativeTime(notification.UpdatedAt, now)}";
        if (notification.SubjectType == "PullRequest")
        {
            Details = notification.SubjectApiUrl is null
                ? PullRequestDetails.Unavailable(notification.Title, "No pull request details are available. Open it on GitHub to learn more.")
                : PullRequestDetails.Loading(notification.Title);
        }
        else if (notification.SubjectType == "Issue")
        {
            Details = notification.SubjectApiUrl is null
                ? IssueDetails.Unavailable(notification.Title, "No issue details are available. Open it on GitHub to learn more.")
                : IssueDetails.Loading(notification.Title);
        }

        Refresh();
    }

    public GitHubNotification Notification { get; }

    internal GitHubAccount? Account { get; }

    internal int AccountGeneration { get; }

    public bool Unread { get; private set; }

    public Uri WebUrl { get; private set; }

    public SubjectDetails? Subject => _subject;

    public Uri? AuthorizeUrl { get; private set; }

    public string SearchText => string.Join(
        ' ',
        Notification.Title,
        Notification.RepositoryFullName,
        Notification.SubjectType,
        Notification.Reason,
        _subject?.State.ToString() ?? string.Empty,
        Unread ? "unread" : string.Empty);

    public void SetUnread(bool unread, Action<Action>? publish = null)
    {
        if (Unread != unread)
        {
            Unread = unread;
            Refresh(publish);
        }
    }

    public void ApplySubject(SubjectDetails subject, Action<Action>? publish = null)
    {
        _subject = subject;
        AuthorizeUrl = null;
        if (subject.WebUrl is { } url)
        {
            WebUrl = url;
        }

        if (Notification.SubjectType == "PullRequest")
        {
            var details = subject.PullRequest is { } pullRequest
                ? new PullRequestDetails(pullRequest)
                : PullRequestDetails.Unavailable(Notification.Title, "Couldn't load pull request details. Try refreshing notifications or open it on GitHub.");
            Publish(publish, () => Details = details);
        }
        else if (Notification.SubjectType == "Issue")
        {
            var details = subject.Issue is { } issue
                ? new IssueDetails(issue, Notification.RepositoryFullName)
                : IssueDetails.Unavailable(Notification.Title, "Couldn't load issue details. Try refreshing notifications or open it on GitHub.");
            Publish(publish, () => Details = details);
        }

        Refresh(publish);
    }

    public void SetSubjectError(string message, Uri? authorizeUrl = null, Action<Action>? publish = null)
    {
        AuthorizeUrl = authorizeUrl;
        if (Notification.SubjectType == "PullRequest")
        {
            Publish(publish, () => Details = PullRequestDetails.Unavailable(Notification.Title, message, authorizeUrl));
        }
        else if (Notification.SubjectType == "Issue")
        {
            Publish(publish, () => Details = IssueDetails.Unavailable(Notification.Title, message, authorizeUrl));
        }

        Refresh(publish);
    }

    private void Refresh(Action<Action>? publish = null)
    {
        Publish(publish, () => Icon = Icons.NotificationIcon(NotificationFormatting.Glyph(Notification.SubjectType), Unread));
        Publish(publish, () => Tags = NotificationFormatting.StateTag(Notification.SubjectType, _subject?.State ?? SubjectState.Unknown) is { } tag ? [tag] : []);

        var more = new List<IContextItem>();
        if (AuthorizeUrl is { } authorize)
        {
            more.Add(new CommandContextItem(new OpenInBrowserCommand(_browser, authorize, "Authorize single sign-on", Icons.Authorize)));
        }

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
        if (_page.SubscriptionPage(this) is { } subscriptionPage)
        {
            more.Add(new CommandContextItem(subscriptionPage));
        }
        if (_page.PullRequestActionsPage(this) is { } pullRequestActionsPage)
        {
            more.Add(new CommandContextItem(pullRequestActionsPage));
        }

        if (Notification.RepositoryWebUrl is { } repo)
        {
            more.Add(new CommandContextItem(new OpenInBrowserCommand(_browser, repo, "Open repository", Icons.Repos)));
        }

        more.Add(new CommandContextItem(new CopyTextCommand(WebUrl.AbsoluteUri) { Name = "Copy link", Icon = Icons.Copy }));
        more.Add(new CommandContextItem(new RefreshNotificationsCommand(_page)));
        Publish(publish, () => MoreCommands = [.. more]);
    }

    private static void Publish(Action<Action>? publish, Action notification)
    {
        if (publish is null)
        {
            notification();
        }
        else
        {
            publish(notification);
        }
    }
}
