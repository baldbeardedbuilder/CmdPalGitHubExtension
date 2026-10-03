// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed class PageListContent(IIconInfo icon, ICommand command)
{
    private readonly Lock _lock = new();
    private ListItem? _current;

    public ListItem Get(string title, string subtitle)
    {
        lock (_lock)
        {
            if (_current is null || _current.Title != title || _current.Subtitle != subtitle)
            {
                _current = new ListItem(command) { Title = title, Subtitle = subtitle, Icon = icon };
            }

            return _current;
        }
    }
}
