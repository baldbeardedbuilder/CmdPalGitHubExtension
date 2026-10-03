// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// The signed in landing page. Everything you can do with GitHub starts here.
/// </summary>
internal sealed partial class HomePage : ListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.home";

    private readonly AuthService _auth;
    private readonly NotificationsPage _notifications;
    private readonly ReposPage _repos;
    private readonly AgentsPage _agents;
    private readonly CodespacesPage _codespaces;
    private readonly CreateCodespacePage _createCodespace;
    private readonly SignOutCommand _signOut;
    private volatile bool _disposed;

    public HomePage(
        AuthService auth,
        NotificationsPage notifications,
        ReposPage repos,
        AgentsPage agents,
        CodespacesPage codespaces,
        CreateCodespacePage createCodespace)
    {
        _auth = auth;
        _notifications = notifications;
        _repos = repos;
        _agents = agents;
        _codespaces = codespaces;
        _createCodespace = createCodespace;
        _signOut = new SignOutCommand(auth);
        Id = PageId;
        Name = "Open";
        Title = "GitHub";
        Icon = Icons.GitHub;
        PlaceholderText = "Search GitHub...";
        _auth.AccountChanged += OnAccountChanged;
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
            new ListItem(_repos) { Title = "Repos", Subtitle = "Find and open repositories", Icon = Icons.Repos, MoreCommands = accountCommands },
            new ListItem(_agents) { Title = "Agents", Subtitle = "Check your Copilot agent tasks", Icon = Icons.Agents, MoreCommands = accountCommands },
            new ListItem(_codespaces)
            {
                Title = "Codespaces",
                Subtitle = "Find and open your codespaces",
                Icon = Icons.Codespaces,
                MoreCommands = [.. accountCommands, new CommandContextItem(_createCodespace)],
            },
        ];
    }

    private static ListItem ComingSoon(string title, IIconInfo icon, IContextItem[] more) =>
        new(new NoOpCommand()) { Title = title, Subtitle = "Coming soon", Icon = icon, MoreCommands = more };

    public void Dispose()
    {
        _disposed = true;
        _auth.AccountChanged -= OnAccountChanged;
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            RaiseItemsChanged();
        }
    }
}
