// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// The signed in landing page. Notifications, repos, and friends will hang off of this.
/// </summary>
internal sealed partial class HomePage : ListPage
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.home";

    private readonly AuthService _auth;
    private readonly SignOutCommand _signOut;

    public HomePage(AuthService auth)
    {
        _auth = auth;
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

        return
        [
            new ListItem(new OpenUrlCommand(new Uri(account.Host.WebUrl, account.Login).AbsoluteUri) { Name = "Open profile" })
            {
                Title = $"Signed in as @{account.Login}",
                Subtitle = account.Host.Name,
                Icon = Icons.Account,
            },
            new ListItem(_signOut)
            {
                Title = "Sign out",
                Subtitle = $"Sign out of {account.Host.Name}",
            },
        ];
    }
}
