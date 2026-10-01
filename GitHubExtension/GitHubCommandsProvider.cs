// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub;

public sealed partial class GitHubCommandsProvider : CommandProvider
{
    private readonly AuthService _auth;
    private readonly SignInPage _signInPage;
    private readonly HomePage _homePage;
    private readonly CommandItem _topLevel;

    public GitHubCommandsProvider()
        : this(AuthService.CreateDefault())
    {
    }

    internal GitHubCommandsProvider(AuthService auth, Func<string>? logoProvider = null)
    {
        _auth = auth;
        _signInPage = new SignInPage(auth, logoProvider);
        _homePage = new HomePage(auth);

        Id = "com.baldbeardedbuilder.cmdpal.github";
        DisplayName = "GitHub";
        Icon = Icons.GitHub;

        _topLevel = new CommandItem(CurrentPage)
        {
            Title = "GitHub",
            Icon = Icons.GitHub,
        };
        UpdateTopLevel();

        _auth.AccountChanged += OnAccountChanged;
    }

    private ICommand CurrentPage => _auth.IsSignedIn ? _homePage : _signInPage;

    public override ICommandItem[] TopLevelCommands() => [_topLevel];

    public override ICommand? GetCommand(string id) => id switch
    {
        SignInPage.PageId => _signInPage,
        HomePage.PageId => _homePage,
        _ => null,
    };

    public override void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        UpdateTopLevel();
        RaiseItemsChanged();
    }

    private void UpdateTopLevel()
    {
        _topLevel.Command = CurrentPage;
        _topLevel.Subtitle = _auth.CurrentAccount is { } account
            ? $"@{account.Login} on {account.Host.Name}"
            : "Sign in to get started";
    }
}
