// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed class PagedListPresentation
{
    private readonly PageListContent _status;
    private readonly PageListContent _more;

    public PagedListPresentation(IIconInfo icon, Func<Task> loadMore)
    {
        var command = new LoadMoreResultsCommand(loadMore);
        _status = new PageListContent(icon, new NoOpCommand());
        _more = new PageListContent(icon, command);
    }

    public IListItem[] Append(IListItem[] items, string title, string subtitle, bool hasMore, bool loading, string? error)
    {
        var result = new List<IListItem>(items);
        if (error is not null)
        {
            result.Add(_status.Get("Couldn't load more results", error));
        }
        else
        {
            result.Add(_status.Get(title, subtitle));
        }

        if (hasMore && !loading)
        {
            result.Add(_more.Get(error is null ? "Load more" : "Retry loading more", "Fetch one more page of results"));
        }

        return [.. result];
    }
}
