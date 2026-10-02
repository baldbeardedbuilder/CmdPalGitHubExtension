// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class OpenInBrowserCommand : InvokableCommand
{
    private readonly IBrowserLauncher _browser;

    public OpenInBrowserCommand(IBrowserLauncher browser, Uri url, string name, IconInfo? icon = null)
    {
        _browser = browser;
        Url = url;
        Name = name;
        if (icon is not null)
        {
            Icon = icon;
        }
    }

    public Uri Url { get; }

    public override ICommandResult Invoke()
    {
        _browser.Open(Url);
        return CommandResult.Dismiss();
    }
}

internal sealed partial class RefreshReposCommand : InvokableCommand
{
    private readonly ReposPage _page;

    public RefreshReposCommand(ReposPage page)
    {
        _page = page;
        Name = "Refresh";
        Icon = Icons.Refresh;
    }

    public override ICommandResult Invoke()
    {
        _ = _page.RefreshAsync();
        return CommandResult.KeepOpen();
    }
}

internal sealed partial class BrowseRepositoryIssuesCommand : InvokableCommand
{
    private readonly RepositoryIssuesPage _page;
    private readonly string _repository;

    public BrowseRepositoryIssuesCommand(RepositoryIssuesPage page, string repository)
    {
        _page = page;
        _repository = repository;
        Name = "Browse issues";
        Icon = Icons.Issues;
    }

    public override ICommandResult Invoke() => _page.Open(_repository);
}

internal sealed partial class BrowseRepositoryPullRequestsCommand : InvokableCommand
{
    private readonly RepositoryPullRequestsPage _page;
    private readonly string _repository;

    public BrowseRepositoryPullRequestsCommand(RepositoryPullRequestsPage page, string repository)
    {
        _page = page;
        _repository = repository;
        Name = "Browse pull requests";
        Icon = Icons.PullRequests;
    }

    public override ICommandResult Invoke() => _page.Open(_repository);
}

internal sealed partial class RefreshRepositoryItemsCommand : InvokableCommand
{
    private readonly Func<Task> _refresh;

    public RefreshRepositoryItemsCommand(Func<Task> refresh, IconInfo icon)
    {
        _refresh = refresh;
        Name = "Refresh";
        Icon = icon;
    }

    public override ICommandResult Invoke()
    {
        _ = _refresh();
        return CommandResult.KeepOpen();
    }
}
