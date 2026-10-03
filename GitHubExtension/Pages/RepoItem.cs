// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

/// <summary>
/// One row on the repos page: name, stars, forks, last push, plus private and language badges.
/// </summary>
internal sealed partial class RepoItem : ListItem
{
    private readonly ReposPage _page;
    private readonly GitHubAccount? _account;
    private readonly int _accountGeneration;
    private RepositoryPage? _repositoryPage;

    public RepoItem(
        ReposPage page,
        GitHubRepository repository,
        IBrowserLauncher browser,
        DateTimeOffset now,
        GitHubAccount? account = null)
    {
        _page = page;
        _account = account ?? page.CurrentAccount;
        _accountGeneration = page.AccountGeneration;
        Repository = repository;
        Title = repository.FullName;
        Subtitle = RepoFormatting.Subtitle(repository, now);
        Icon = Icons.Repos;
        Tags = RepoFormatting.Tags(repository);

        var repoBase = repository.WebUrl.AbsoluteUri.TrimEnd('/') + "/";
        var more = new List<IContextItem>
        {
            new CommandContextItem(new OpenInBrowserCommand(browser, repository.WebUrl, "Open on GitHub", Icons.Repos)),
            new CommandContextItem(new OpenInBrowserCommand(browser, new Uri(repoBase + "issues"), "Open issues", Icons.Issues)),
            new CommandContextItem(new OpenInBrowserCommand(browser, new Uri(repoBase + "pulls"), "Open pull requests", Icons.PullRequests)),
        };

        if (page.Actions is not null)
        {
            more.Add(new RepositoryActionsContextItem(this));
        }

        if (repository.CloneUrl is { } clone)
        {
            more.Add(new CommandContextItem(new CopyTextCommand(clone.AbsoluteUri) { Name = "Copy clone URL", Icon = Icons.Copy }));
        }

        more.Add(new CommandContextItem(new CopyTextCommand(repository.FullName) { Name = "Copy name", Icon = Icons.Copy }));
        more.Add(new CommandContextItem(new RefreshReposCommand(page)));
        if (page.StarPage(repository, _account, _accountGeneration) is { } starPage)
        {
            more.Add(new CommandContextItem(starPage));
        }

        if (page.WatchPage(repository, _account, _accountGeneration) is { } watchPage)
        {
            more.Add(new CommandContextItem(watchPage));
        }

        if (page.RepositoryAgents(repository.FullName, _account, _accountGeneration) is { } agentsPage)
        {
            more.Add(new CommandContextItem(agentsPage));
        }
        if (page.WorkSearch is { } search)
        {
            more.Add(new CommandContextItem(search));
        }

        MoreCommands = [.. more];
    }

    public GitHubRepository Repository { get; }

    public RepositoryPage RepositoryPage => Command as RepositoryPage ?? throw new ObjectDisposedException(nameof(RepoItem));

    public override ICommand? Command
    {
        get
        {
            if (!_page.CanNavigate(_account, _accountGeneration))
            {
                return null;
            }

            if (_repositoryPage is null || _repositoryPage.IsDisposed)
            {
                _repositoryPage = _page.CreateRepositoryPage(Repository, _account, _accountGeneration);
            }

            return _repositoryPage;
        }

        set => base.Command = value;
    }

    public bool Matches(string[] terms) => terms.All(t =>
        Repository.FullName.Contains(t, StringComparison.OrdinalIgnoreCase)
        || (Repository.Description?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Repository.Language?.Equals(t, StringComparison.OrdinalIgnoreCase) ?? false));

    private sealed partial class RepositoryActionsContextItem(RepoItem item) : CommandContextItem(new NoOpCommand())
    {
        public override ICommand? Command
        {
            get => (item.Command as RepositoryPage)?.Actions;
            set => base.Command = value;
        }
    }
}
