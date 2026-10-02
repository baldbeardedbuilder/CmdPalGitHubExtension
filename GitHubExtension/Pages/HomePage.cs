// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// The signed in landing page. Everything you can do with GitHub starts here.
/// </summary>
internal sealed partial class HomePage : ListPage
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.home";

    private readonly AuthService _auth;
    private readonly NotificationsPage _notifications;
    private readonly SignOutCommand _signOut;

    public HomePage(AuthService auth, NotificationsPage notifications)
    {
        _auth = auth;
        _notifications = notifications;
        _signOut = new SignOutCommand(auth);
        Id = PageId;
        Name = "Open";
        Title = "GitHub";
        Icon = Icons.GitHub;
        PlaceholderText = "Search GitHub...";
        _auth.AccountChanged += (_, _) => RaiseItemsChanged();
    }

    public override IListItem[] GetItems()
    {
        var account = _auth.CurrentAccount;
        if (account is null)
        {
            return [];
        }

        IContextItem[] accountCommands =
        [
            new CommandContextItem(new OpenUrlCommand(new Uri(account.Host.WebUrl, account.Login).AbsoluteUri) { Name = $"Open @{account.Login} profile", Icon = Icons.Account }),
            new CommandContextItem(_signOut),
        ];

        return
        [
            new ListItem(_notifications) { Title = "Notifications", Subtitle = "Your GitHub inbox", Icon = Icons.Notifications, MoreCommands = accountCommands },
            ComingSoon("Saved Queries", Icons.SavedQueries, accountCommands),
            ComingSoon("Repos", Icons.Repos, accountCommands),
            ComingSoon("Agents", Icons.Agents, accountCommands),
            ComingSoon("Codespaces", Icons.Codespaces, accountCommands),
        ];
    }

    private static ListItem ComingSoon(string title, IIconInfo icon, IContextItem[] more) =>
        new(new NoOpCommand()) { Title = title, Subtitle = "Coming soon", Icon = icon, MoreCommands = more };
}
