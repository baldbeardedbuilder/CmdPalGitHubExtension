// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed class PageEmptyContent(IIconInfo icon, ICommand refreshCommand)
{
    private readonly Lock _lock = new();
    private readonly NoOpCommand _noOp = new();
    private CommandItem? _current;

    public CommandItem Get(string title, string subtitle, bool refresh = false)
    {
        lock (_lock)
        {
            var command = refresh ? refreshCommand : (ICommand)_noOp;
            // Reentrant host reads must not publish another notification for the same content.
            if (_current is null || _current.Title != title || _current.Subtitle != subtitle || !ReferenceEquals(_current.Command, command))
            {
                _current = new CommandItem(command) { Title = title, Subtitle = subtitle, Icon = icon };
            }

            return _current;
        }
    }
}
