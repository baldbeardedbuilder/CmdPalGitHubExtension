using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;
using BaldBeardedBuilder.CmdPal.GitHub.Search;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class ReposPage
{
    private IssueSearchPage? _workSearch;
    private IContextItem[]? _workSearchCommands;
    internal IssueSearchPage? WorkSearch
    {
        get
        {
            lock (_lock)
            {
                if (_load.Disposed || _issueSearchClient is not { } client) { return null; }
                return _workSearch ??= new(_auth, client, _browser, detailFactories: _workItemDetailFactories);
            }
        }
    }

    private IContextItem[] WorkSearchCommands() => _workSearchCommands ??= WorkSearch is { } page
        ? [new CommandContextItem(page)] : [];
    private readonly Dictionary<string, WeakReference<AgentsPage>> _agentPages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WeakReference<RepositoryWatchPage>> _watchPages = new(StringComparer.OrdinalIgnoreCase);

    internal RepositoryWatchPage? WatchPage(GitHubRepository repository, GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            if (_client is not IRepositoryWatchingClient client || !CanNavigate(account, generation)) { return null; }
            foreach (var key in _watchPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
            {
                _watchPages.Remove(key);
            }

            if (!_watchPages.TryGetValue(repository.FullName, out var reference) || !reference.TryGetTarget(out var page))
            {
                page = new(client, _starExecutor, account!, repository.FullName, () => CanNavigate(account, generation), _browser);
                _watchPages[repository.FullName] = new(page);
            }

            return page;
        }
    }

    private RepositoryWatchPage[] TakeWatchPages()
    {
        var pages = _watchPages.Values.Select(reference => reference.TryGetTarget(out var page) ? page : null)
            .OfType<RepositoryWatchPage>().ToArray();
        _watchPages.Clear();
        return pages;
    }

    internal AgentsPage? RepositoryAgents(string repository, GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            if (_agentsClient is null || !CanNavigate(account, generation)) { return null; }
            foreach (var key in _agentPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
            {
                _agentPages.Remove(key);
            }

            if (!_agentPages.TryGetValue(repository, out var reference) || !reference.TryGetTarget(out var page))
            {
                page = new(_auth, _agentsClient, _browser, _time, new(Repository: repository));
                _agentPages[repository] = new(page);
            }

            return page;
        }
    }

    private void DisposeBrowsingPages()
    {
        RepositoryWatchPage[] watchPages;
        AgentsPage[] agentsPages;
        IssueSearchPage? workSearch;
        lock (_lock)
        {
            watchPages = TakeWatchPages();
            agentsPages = _agentPages.Values.Select(reference => reference.TryGetTarget(out var page) ? page : null).OfType<AgentsPage>().ToArray();
            _agentPages.Clear();
            workSearch = _workSearch;
            _workSearch = null;
            _workSearchCommands = null;
        }

        foreach (var page in watchPages) { page.Dispose(); }
        foreach (var page in agentsPages) { page.Dispose(); }
        workSearch?.Dispose();
    }
}
