// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Actions;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Issues;
using BaldBeardedBuilder.CmdPal.GitHub.Notifications;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;
using BaldBeardedBuilder.CmdPal.GitHub.PullRequests;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub;

public sealed partial class GitHubCommandsProvider : CommandProvider
{
    private readonly AuthService _auth;
    private readonly SignInPage _signInPage;
    private readonly NotificationsPage _notificationsPage;
    private readonly IssueDetailsPage _issueDetailsPage;
    private readonly RepositoryIssuesPage _repositoryIssuesPage;
    private readonly RepositoryPullRequestsPage _repositoryPullRequestsPage;
    private readonly ReposPage _reposPage;
    private readonly AgentsPage _agentsPage;
    private readonly ActionsPage _actionsPage;
    private readonly CodespacesPage _codespacesPage;
    private readonly HomePage _homePage;
    private readonly CommandItem _topLevel;

    public GitHubCommandsProvider()
        : this(AuthService.CreateDefault())
    {
    }

    internal GitHubCommandsProvider(
        AuthService auth,
        Func<string>? logoProvider = null,
        INotificationsClient? notificationsClient = null,
        IBrowserLauncher? browser = null,
        IRepositoriesClient? repositoriesClient = null,
        IIssuesClient? issuesClient = null,
        ICodespacesClient? codespacesClient = null,
        IActionsClient? actionsClient = null,
        IAgentsClient? agentsClient = null,
        IPullRequestsClient? pullRequestsClient = null)
    {
        _auth = auth;
        browser ??= new ShellBrowserLauncher();
        HttpClient? http = null;
        HttpClient Http() => http ??= new HttpClient();
        _signInPage = new SignInPage(auth, logoProvider);
        issuesClient ??= new IssuesClient(Http());
        _issueDetailsPage = new IssueDetailsPage(auth, issuesClient, browser);
        _repositoryIssuesPage = new RepositoryIssuesPage(auth, issuesClient, browser);
        _repositoryPullRequestsPage = new RepositoryPullRequestsPage(auth, pullRequestsClient ?? new PullRequestsClient(Http()), browser);
        _notificationsPage = new NotificationsPage(auth, notificationsClient ?? new NotificationsClient(Http()), browser, issueDetails: _issueDetailsPage);
        _agentsPage = new AgentsPage(auth, agentsClient ?? new AgentsClient(Http()), browser);
        _actionsPage = new ActionsPage(auth, actionsClient ?? new ActionsClient(Http()), browser);
        _reposPage = new ReposPage(
            auth,
            repositoriesClient ?? new RepositoriesClient(Http()),
            browser,
            _repositoryIssuesPage,
            _repositoryPullRequestsPage,
            actions: _actionsPage);
        _codespacesPage = new CodespacesPage(auth, codespacesClient ?? new CodespacesClient(Http()), browser);
        _homePage = new HomePage(auth, _notificationsPage, _reposPage, _agentsPage, _codespacesPage);

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
        NotificationsPage.PageId => _notificationsPage,
        IssueDetailsPage.PageId => _issueDetailsPage,
        RepositoryIssuesPage.PageId => _repositoryIssuesPage,
        RepositoryPullRequestsPage.PageId => _repositoryPullRequestsPage,
        ReposPage.PageId => _reposPage,
        RepositoryPage.PageId => _reposPage.RepositoryPage,
        AgentsPage.PageId => _agentsPage,
        ActionsPage.PageId => _actionsPage,
        CodespacesPage.PageId => _codespacesPage,
        _ => null,
    };

    public override void Dispose()
    {
        _auth.AccountChanged -= OnAccountChanged;
        _reposPage.Dispose();
        _agentsPage.Dispose();
        _actionsPage.Dispose();
        _codespacesPage.Dispose();
        _repositoryIssuesPage.Dispose();
        _repositoryPullRequestsPage.Dispose();
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
