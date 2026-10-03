// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;
using BaldBeardedBuilder.CmdPal.GitHub.Repositories;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class ReposPage
{
    internal const string StarredPageId = "com.baldbeardedbuilder.cmdpal.github.starred";
    private readonly IRepositoryStarsClient? _starsClient;
    private readonly bool _starred;
    private readonly MutationExecutor _starExecutor;
    private readonly Dictionary<string, WeakReference<RepositoryStarPage>> _starPages = new(StringComparer.OrdinalIgnoreCase);
    private PagedListPresentation? _starredPagination;

    internal ReposPage CreateStarredPage() =>
        new(_auth, _client, _browser, _repositoryIssuesPage, _repositoryPullRequestsPage, _time, _searchDelay,
            Actions, _agentsClient, _starsClient, starred: true);

    internal RepositoryStarPage? StarPage(GitHubRepository repository, GitHubAccount? account, int generation)
    {
        lock (_lock)
        {
            if (_starsClient is null || !CanNavigate(account, generation))
            {
                return null;
            }

            foreach (var key in _starPages.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key).ToArray())
            {
                _starPages.Remove(key);
            }

            if (!_starPages.TryGetValue(repository.FullName, out var reference) || !reference.TryGetTarget(out var page))
            {
                page = new RepositoryStarPage(_starsClient, _starExecutor, account!, repository.FullName,
                    () => CanNavigate(account, generation), _browser, RefreshAsync);
                _starPages[repository.FullName] = new(page);
            }

            return page;
        }
    }

    private RepositoryStarPage[] TakeStarPages()
    {
        var pages = _starPages.Values.Select(reference => reference.TryGetTarget(out var page) ? page : null)
            .OfType<RepositoryStarPage>().ToArray();
        _starPages.Clear();
        return pages;
    }

    private IListItem[] StarredItems(RepoItem[] repositories, string query, string? error)
    {
        Uri? next;
        bool fetching;
        lock (_lock)
        {
            next = _load.NextPage;
            fetching = _load.Fetching;
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        IListItem[] items = terms.Length == 0 ? repositories : [.. repositories.Where(item => item.Matches(terms))];
        EmptyContent = error is not null ? Empty("Couldn't load starred repositories", error, refresh: true)
            : Empty(next is null ? "No starred repositories found" : "No loaded starred repositories match",
                next is null ? "Star a repository to save it here." : "Load more to search the rest of your starred repositories.");
        if (next is null && !fetching && error is null)
        {
            return items;
        }

        _starredPagination ??= new(Icons.Repos, () => StartLoad(reset: false));
        return _starredPagination.Append(items, "Starred repositories",
            "Filtering the starred repositories loaded so far.", next is not null, fetching, error);
    }
}
