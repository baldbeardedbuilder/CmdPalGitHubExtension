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
using BaldBeardedBuilder.CmdPal.GitHub.Search;

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
    private readonly ReposPage? _starredReposPage;
    private readonly AgentsPage _agentsPage;
    private readonly ActionsPage _actionsPage;
    private readonly CodespacesPage _codespacesPage;
    private readonly CreateCodespacePage _createCodespacePage;
    private readonly HomePage _homePage;
    private readonly CommandItem _topLevel;
    private readonly HttpClient? _ownedHttp;
    private readonly bool _ownsAuth;
    private int _disposed;

    public GitHubCommandsProvider()
        : this(AuthService.CreateDefault(), ownsAuth: true)
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
        IPullRequestsClient? pullRequestsClient = null,
        IPullRequestActionsClient? pullRequestActionsClient = null,
        IRepositoryStarsClient? repositoryStarsClient = null,
        IThreadSubscriptionsClient? threadSubscriptionsClient = null,
        bool ownsAuth = false,
        Func<HttpClient>? httpFactory = null,
        IIssueSearchClient? issueSearchClient = null)
    {
        _auth = auth;
        _ownsAuth = ownsAuth;
        browser ??= new ShellBrowserLauncher();
        HttpClient? http = null;
        HttpClient Http() => http ??= httpFactory?.Invoke() ?? new HttpClient();
        _signInPage = new SignInPage(auth, logoProvider);
        repositoriesClient ??= new RepositoriesClient(Http());
        issueSearchClient ??= new IssueSearchClient(Http());
        codespacesClient ??= new CodespacesClient(Http());
        Func<string, int, string?, ICommand?> codespaceFactory = (repository, number, head) =>
            auth.CurrentAccount?.Host.IsGitHubDotCom == true
                ? ContextualCodespacePage.ForPullRequest(auth, codespacesClient, browser, repository, number, head)
                : null;
        var resolvedPullRequestActionsClient = pullRequestActionsClient ?? new PullRequestActionsClient(Http());
        issuesClient ??= new IssuesClient(Http());
        pullRequestsClient ??= new PullRequestsClient(Http());
        var workItemDetailFactories = new WorkItemDetailFactories(
            PullRequestDetails: (account, repository, number, isCurrent) =>
                isCurrent() && resolvedPullRequestActionsClient is IPullRequestFeatureClient features
                    ? new PullRequestDetailsPage(auth, features, account, repository, number,
                        codespaceFactory, isCurrent)
                    : null,
            Conversation: (account, repository, number, pullRequest, isCurrent) =>
            {
                var client = pullRequest
                    ? pullRequestsClient as IIssueConversationClient
                    : issuesClient as IIssueConversationClient;
                return isCurrent() && client is not null
                    ? new IssueConversationPage(auth, client, account, repository, number,
                        pullRequest ? "Pull request" : "Issue", isCurrent)
                    : null;
            });
        _issueDetailsPage = new IssueDetailsPage(auth, issuesClient, browser);
        _repositoryIssuesPage = new RepositoryIssuesPage(auth, issuesClient, browser);
        _repositoryPullRequestsPage = new RepositoryPullRequestsPage(
            auth,
            pullRequestsClient,
            browser,
            mergeClient: new PullRequestMergeClient(Http()),
            actionsClient: resolvedPullRequestActionsClient,
            contextualCodespaceFactory: codespaceFactory);
        var resolvedNotificationsClient = notificationsClient ?? new NotificationsClient(Http());
        _notificationsPage = new NotificationsPage(
            auth,
            resolvedNotificationsClient,
            browser,
            issueDetails: _issueDetailsPage,
            subscriptionsClient: threadSubscriptionsClient,
            pullRequestActionsClient: resolvedPullRequestActionsClient,
            workItemDetailFactories: workItemDetailFactories);
        agentsClient ??= new AgentsClient(Http());
        _agentsPage = new AgentsPage(auth, agentsClient, browser);
        _actionsPage = new ActionsPage(auth, actionsClient ?? new ActionsClient(Http()), browser);
        _reposPage = new ReposPage(
            auth,
            repositoriesClient,
            browser,
            _repositoryIssuesPage,
            _repositoryPullRequestsPage,
            actions: _actionsPage,
            agentsClient: agentsClient,
            starsClient: repositoryStarsClient,
            workItemDetailFactories: workItemDetailFactories,
            issueSearchClient: issueSearchClient,
            codespacesClient: codespacesClient);
        if (repositoryStarsClient is not null || repositoriesClient is IRepositoryStarsClient)
        {
            _starredReposPage = _reposPage.CreateStarredPage();
        }
        _createCodespacePage = new CreateCodespacePage(auth, codespacesClient, browser);
        _codespacesPage = new CodespacesPage(auth, codespacesClient, browser, createPage: _createCodespacePage);
        _homePage = new HomePage(auth, _notificationsPage, _reposPage, _agentsPage, _codespacesPage, _createCodespacePage, _starredReposPage);

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
        _ownedHttp = http;
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
        IssueSearchPage.PageId => _reposPage.WorkSearch,
        ReposPage.StarredPageId when _starredReposPage is not null => _starredReposPage,
        AgentsPage.PageId => _agentsPage,
        ActionsPage.PageId => _actionsPage,
        CodespacesPage.PageId => _codespacesPage,
        CreateCodespacePage.PageId => _createCodespacePage,
        _ => null,
    };

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _auth.AccountChanged -= OnAccountChanged;
        _homePage.Dispose();
        _signInPage.Dispose();
        _notificationsPage.Dispose();
        _issueDetailsPage.Dispose();
        _reposPage.Dispose();
        _starredReposPage?.Dispose();
        _agentsPage.Dispose();
        _actionsPage.Dispose();
        _codespacesPage.Dispose();
        _createCodespacePage.Dispose();
        _repositoryIssuesPage.Dispose();
        _repositoryPullRequestsPage.Dispose();
        _ownedHttp?.Dispose();
        if (_ownsAuth)
        {
            _auth.Dispose();
        }
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (_disposed != 0)
        {
            return;
        }

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
