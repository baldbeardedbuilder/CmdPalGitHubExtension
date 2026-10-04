// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Agents;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class RepositoryPage : ListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.repository";

    private readonly IBrowserLauncher _browser;
    private readonly ActionsPage? _actionsTemplate;
    private readonly RepositoryIssuesPage? _issuesTemplate;
    private readonly RepositoryPullRequestsPage? _pullRequestsTemplate;
    private readonly AuthService? _auth;
    private readonly IAgentsClient? _agentsClient;
    private readonly RepositoryStarPage? _starPage;
    private readonly RepositoryWatchPage? _watchPage;
    private readonly AgentsPage? _repositoryAgents;
    private readonly ICodespacesClient? _codespacesClient;
    private GitHubRepository _repository;
    private readonly Lock _lock = new();
    private ActionsPage? _actions;
    private RepositoryIssuesPage? _issuesPage;
    private RepositoryPullRequestsPage? _pullRequestsPage;
    private CreateAgentTaskPage? _createAgentTaskPage;
    private ContextualCodespacePage? _contextualCodespacePage;
    private bool _initialized;
    private bool _disposed;
    private IListItem[] _items = [];

    public RepositoryPage(
        IBrowserLauncher browser,
        ActionsPage? actions,
        GitHubRepository repository,
        RepositoryIssuesPage? issuesPage = null,
        RepositoryPullRequestsPage? pullRequestsPage = null,
        AuthService? auth = null,
        IAgentsClient? agentsClient = null,
        RepositoryStarPage? starPage = null,
        RepositoryWatchPage? watchPage = null,
        AgentsPage? repositoryAgents = null,
        ICodespacesClient? codespacesClient = null)
    {
        _browser = browser;
        _actionsTemplate = actions;
        _issuesTemplate = issuesPage;
        _pullRequestsTemplate = pullRequestsPage;
        _auth = auth;
        _agentsClient = agentsClient;
        _starPage = starPage;
        _watchPage = watchPage;
        _repositoryAgents = repositoryAgents;
        _codespacesClient = codespacesClient;
        _repository = repository;
        Name = "Open";
        Icon = Icons.Repos;
        Title = repository.FullName;
        PlaceholderText = $"Search in {repository.FullName}...";
    }

    private void SetRepository(GitHubRepository repository)
    {
        var repoBase = repository.WebUrl.AbsoluteUri.TrimEnd('/') + "/";
        var open = new OpenInBrowserCommand(_browser, repository.WebUrl, "Open on GitHub", Icons.Repos);
        IContextItem[] more =
        [
            new CommandContextItem(open),
            new CommandContextItem(new CopyTextCommand(repository.FullName) { Name = "Copy name", Icon = Icons.Copy }),
        ];
        var actions = _actions is null
            ? (ICommand)new OpenInBrowserCommand(_browser, new Uri(repoBase + "actions"), "Open on GitHub", Icons.Actions)
            : _actions;

        var items = new List<IListItem>
        {
            new ListItem(open) { Title = repository.FullName, Subtitle = repository.Description ?? string.Empty, Icon = Icons.Repos, MoreCommands = more },
            new PinnableListItem(_issuesPage is null
                ? new OpenInBrowserCommand(_browser, new Uri(repoBase + "issues"), "Open on GitHub", Icons.Issues)
                : _issuesPage)
            {
                Title = "Issues", Subtitle = _issuesPage is null ? "Open issues on GitHub" : $"Browse issues in {repository.FullName}",
                Icon = Icons.Issues, MoreCommands = more,
            },
            new PinnableListItem(_pullRequestsPage is null
                ? new OpenInBrowserCommand(_browser, new Uri(repoBase + "pulls"), "Open on GitHub", Icons.PullRequests)
                : _pullRequestsPage)
            {
                Title = "Pull Requests", Subtitle = _pullRequestsPage is null
                    ? "Open pull requests on GitHub"
                    : $"Browse pull requests in {repository.FullName}",
                Icon = Icons.PullRequests, MoreCommands = more,
            },
            new PinnableListItem(actions)
            {
                Title = "Actions", Subtitle = _actions is null ? "Open workflows on GitHub" : "Browse workflow runs",
                Icon = Icons.Actions, MoreCommands = more,
            },
        };
        if (_starPage is not null)
        {
            items.Add(new ListItem(_starPage) { Title = "Manage star", Subtitle = "Check and change your personal star", Icon = Icons.Repos });
        }

        if (_watchPage is not null)
        {
            items.Add(new ListItem(_watchPage)
            {
                Title = "Manage watching",
                Subtitle = "Watch, unwatch, or ignore this repository",
                Icon = Icons.Notifications,
            });
        }

        if (_repositoryAgents is not null)
        {
            items.Add(new PinnableListItem(_repositoryAgents)
            {
                Title = "Copilot tasks",
                Subtitle = "Browse current and archived repository tasks",
                Icon = Icons.Agents,
                MoreCommands = more,
            });
        }

        if (_contextualCodespacePage is not null)
        {
            items.Add(new ListItem(_contextualCodespacePage)
            {
                Title = "Open Codespace",
                Subtitle = "Find or create a development environment for this repository",
                Icon = Icons.Codespaces,
                MoreCommands = more,
            });
        }

        if (_createAgentTaskPage is not null)
        {
            items.Add(new ListItem(_createAgentTaskPage)
            {
                Title = "Start Copilot task",
                Subtitle = "Send work to a Copilot cloud agent",
                Icon = Icons.Agents,
                MoreCommands = more,
            });
        }

        items.Add(new ListItem(new OpenInBrowserCommand(_browser, new Uri(repoBase + "discussions"), "Open on GitHub", Icons.Discussions))
        {
            Title = "Discussions",
            Subtitle = "Open discussions on GitHub",
            Icon = Icons.Discussions,
            MoreCommands = more,
        });
        _items = [.. items];
    }

    public override IListItem[] GetItems()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return [];
            }

            if (!_initialized)
            {
                _actions ??= _actionsTemplate?.ForRepository(_repository.FullName, this);
                _issuesPage ??= _issuesTemplate?.ForRepository(_repository.FullName, this);
                _pullRequestsPage ??= _pullRequestsTemplate?.ForRepository(_repository.FullName, this);
                _createAgentTaskPage ??= _auth is not null && _agentsClient is not null
                    ? new CreateAgentTaskPage(_auth, _agentsClient, _repository) { Owner = this }
                    : null;
                _contextualCodespacePage ??= _auth?.CurrentAccount?.Host.IsGitHubDotCom == true && _codespacesClient is not null
                    ? ContextualCodespacePage.ForRepository(
                        _auth, _codespacesClient, _browser, _repository.FullName, null)
                    : null;
                SetRepository(_repository);
                _initialized = true;
            }

            return _items;
        }
    }

    internal void UpdateRepository(GitHubRepository repository)
    {
        lock (_lock)
        {
            if (!_disposed && _repository != repository)
            {
                _repository = repository;
                _initialized = false;
            }
        }
    }

    internal ActionsPage? Actions
    {
        get
        {
            lock (_lock)
            {
                return _disposed ? null : _actions ??= _actionsTemplate?.ForRepository(_repository.FullName, this);
            }
        }
    }

    internal bool IsDisposed => _disposed;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _items = [];
        }

        _actions?.Dispose();
        _issuesPage?.Dispose();
        _pullRequestsPage?.Dispose();
        _createAgentTaskPage?.Dispose();
        _contextualCodespacePage?.Dispose();
    }

    internal void Reset()
    {
        Dispose();
        Title = "Repository";
        PlaceholderText = "Search repository sections...";
        SearchText = string.Empty;
        RaiseItemsChanged();
    }
}
