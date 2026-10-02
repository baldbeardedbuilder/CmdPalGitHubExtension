// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class RepositoryPage : ListPage, IDisposable
{
    public const string PageId = "com.baldbeardedbuilder.cmdpal.github.repository";

    private readonly IBrowserLauncher _browser;
    private readonly ActionsPage? _actions;
    private readonly RepositoryIssuesPage? _issuesPage;
    private readonly RepositoryPullRequestsPage? _pullRequestsPage;
    private IListItem[] _items = [];

    public RepositoryPage(
        IBrowserLauncher browser,
        ActionsPage? actions,
        GitHubRepository repository,
        RepositoryIssuesPage? issuesPage = null,
        RepositoryPullRequestsPage? pullRequestsPage = null)
    {
        _browser = browser;
        _actions = actions?.ForRepository(repository.FullName);
        _issuesPage = issuesPage?.ForRepository(repository.FullName);
        _pullRequestsPage = pullRequestsPage?.ForRepository(repository.FullName);
        Id = $"{PageId}.{Uri.EscapeDataString(repository.FullName)}";
        Name = "Open";
        Icon = Icons.Repos;
        SetRepository(repository);
    }

    private void SetRepository(GitHubRepository repository)
    {
        Title = repository.FullName;
        PlaceholderText = $"Search in {repository.FullName}...";
        SearchText = string.Empty;
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

        _items =
        [
            new ListItem(open) { Title = repository.FullName, Subtitle = repository.Description ?? string.Empty, Icon = Icons.Repos, MoreCommands = more },
            new ListItem(_issuesPage is null
                ? new OpenInBrowserCommand(_browser, new Uri(repoBase + "issues"), "Open on GitHub", Icons.Issues)
                : _issuesPage)
            {
                Title = "Issues", Subtitle = _issuesPage is null ? "Open issues on GitHub" : $"Browse issues in {repository.FullName}",
                Icon = Icons.Issues, MoreCommands = more,
            },
            new ListItem(_pullRequestsPage is null
                ? new OpenInBrowserCommand(_browser, new Uri(repoBase + "pulls"), "Open on GitHub", Icons.PullRequests)
                : _pullRequestsPage)
            {
                Title = "Pull Requests", Subtitle = _pullRequestsPage is null
                    ? "Open pull requests on GitHub"
                    : $"Browse pull requests in {repository.FullName}",
                Icon = Icons.PullRequests, MoreCommands = more,
            },
            new ListItem(actions)
            {
                Title = "Actions", Subtitle = _actions is null ? "Open workflows on GitHub" : "Browse workflow runs",
                Icon = Icons.Actions, MoreCommands = more,
            },
            new ListItem(new OpenInBrowserCommand(_browser, new Uri(repoBase + "discussions"), "Open on GitHub", Icons.Discussions))
            {
                Title = "Discussions", Subtitle = "Open discussions on GitHub", Icon = Icons.Discussions, MoreCommands = more,
            },
        ];
        RaiseItemsChanged();
    }

    public override IListItem[] GetItems() => _items;

    internal ActionsPage? Actions => _actions;

    public void Dispose()
    {
        _actions?.Dispose();
        _issuesPage?.Dispose();
        _pullRequestsPage?.Dispose();
    }

    internal void Reset()
    {
        _items = [];
        Title = "Repository";
        PlaceholderText = "Search repository sections...";
        SearchText = string.Empty;
        RaiseItemsChanged();
        Dispose();
    }
}
