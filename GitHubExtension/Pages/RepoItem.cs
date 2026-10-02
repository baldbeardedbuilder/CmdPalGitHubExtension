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
    public RepoItem(ReposPage page, GitHubRepository repository, IBrowserLauncher browser, DateTimeOffset now)
    {
        Repository = repository;
        Command = new OpenInBrowserCommand(browser, repository.WebUrl, "Open", Icons.Repos);
        Title = repository.FullName;
        Subtitle = RepoFormatting.Subtitle(repository, now);
        Icon = Icons.Repos;
        Tags = RepoFormatting.Tags(repository);

        var repoBase = repository.WebUrl.AbsoluteUri.TrimEnd('/') + "/";
        var more = new List<IContextItem>
        {
            new CommandContextItem(new OpenInBrowserCommand(browser, new Uri(repoBase + "issues"), "Open issues", Icons.Issues)),
            new CommandContextItem(new OpenInBrowserCommand(browser, new Uri(repoBase + "pulls"), "Open pull requests", Icons.PullRequests)),
        };

        if (page.Actions is { } actions)
        {
            more.Add(new CommandContextItem(new OpenActionsCommand(actions, repository.FullName)));
        }

        if (repository.CloneUrl is { } clone)
        {
            more.Add(new CommandContextItem(new CopyTextCommand(clone.AbsoluteUri) { Name = "Copy clone URL", Icon = Icons.Copy }));
        }

        more.Add(new CommandContextItem(new CopyTextCommand(repository.FullName) { Name = "Copy name", Icon = Icons.Copy }));
        more.Add(new CommandContextItem(new RefreshReposCommand(page)));
        MoreCommands = [.. more];
    }

    public GitHubRepository Repository { get; }

    public bool Matches(string[] terms) => terms.All(t =>
        Repository.FullName.Contains(t, StringComparison.OrdinalIgnoreCase)
        || (Repository.Description?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Repository.Language?.Equals(t, StringComparison.OrdinalIgnoreCase) ?? false));
}
