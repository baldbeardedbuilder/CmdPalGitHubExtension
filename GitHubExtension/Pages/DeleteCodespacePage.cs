// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Globalization;
using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class DeleteCodespacePage : ListPage
{
    private readonly CodespacesPage _page;
    private readonly CodespaceItem _item;
    private readonly GitHubAccount? _account;
    private readonly int _generation;
    private readonly CancellationToken _token;
    private readonly Lock _lock = new();
    private GitHubCodespace? _details;
    private string _status = "Loading fresh git status...";
    private bool _started;
    private bool _finished;
    private Task _currentOperation = Task.CompletedTask;

    internal DeleteCodespacePage(CodespacesPage page, CodespaceItem item)
    {
        _page = page;
        _item = item;
        (_account, _generation, _token) = page.DeleteContext();
        Id = $"com.baldbeardedbuilder.cmdpal.github.delete-codespace.{item.Codespace.Name}";
        Name = "Delete Codespace";
        Title = "Delete Codespace";
        Icon = Icons.Codespaces;
    }

    internal Task CurrentOperation
    {
        get { lock (_lock) { return _currentOperation; } }
    }

    public override IListItem[] GetItems()
    {
        bool finished;
        string status;
        lock (_lock)
        {
            finished = _finished;
            status = _status;
        }

        if (finished && _page.IsDeleteContextCurrent(_account, _generation))
        {
            return [
                new ListItem { Title = _item.Codespace.Name, Subtitle = _item.Codespace.RepositoryFullName },
                new ListItem { Title = status },
                new ListItem(new DeleteConfirmationCommand(this, true)) { Title = "Back" },
            ];
        }

        if (!_page.CanDelete(_item, _account, _generation))
        {
            return [new ListItem { Title = "This codespace is no longer current", Subtitle = "Return to Codespaces and refresh." }];
        }

        GitHubCodespace? details;
        lock (_lock)
        {
            if (!_started && !_finished)
            {
                _started = true;
                _currentOperation = Task.Run(LoadDetailsAsync);
            }

            details = _details;
            status = _status;
            finished = _finished;
        }

        var codespace = details ?? _item.Codespace;
        var items = new List<IListItem>
        {
            new ListItem { Title = codespace.Name, Subtitle = codespace.RepositoryFullName },
            new ListItem { Title = "Permanent deletion", Subtitle = "This cannot be undone. Files and changes stored only in this codespace will be lost. Push or back up work first; reported status is not a guarantee." },
            new ListItem { Title = status },
        };
        if (details is not null)
        {
            if (codespace.Ahead is not null || codespace.Behind is not null)
            {
                items.Add(new ListItem { Title = $"Commits ahead: {codespace.Ahead?.ToString(CultureInfo.CurrentCulture) ?? "Unknown"}; behind: {codespace.Behind?.ToString(CultureInfo.CurrentCulture) ?? "Unknown"}" });
            }

            items.Add(new ListItem { Title = Warning("Uncommitted changes", codespace.HasUncommittedChanges) });
            items.Add(new ListItem { Title = Warning("Unpushed changes", codespace.HasUnpushedChanges) });
            if (!finished)
            {
                items.Add(new ListItem(new DeleteConfirmationCommand(this, false)) { Title = "Permanently delete this codespace" });
            }
        }

        items.Add(new ListItem(new DeleteConfirmationCommand(this, true)) { Title = finished ? "Back" : "Cancel" });
        return [.. items];
    }

    internal ICommandResult Cancel()
    {
        lock (_lock)
        {
            if (!_finished)
            {
                _finished = true;
                _status = "Cancelled. No deletion requested.";
            }
        }

        RaiseItemsChanged();
        return CommandResult.GoBack();
    }

    internal Task ConfirmAsync()
    {
        lock (_lock)
        {
            if (_finished || _details is null || !_page.CanDelete(_item, _account, _generation))
            {
                return _currentOperation;
            }

            _finished = true;
            _status = "Requesting deletion and checking GitHub's complete codespaces list...";
            _currentOperation = Task.Run(DeleteAsync);
        }

        RaiseItemsChanged();
        return CurrentOperation;
    }

    private async Task LoadDetailsAsync()
    {
        try
        {
            _token.ThrowIfCancellationRequested();
            var details = await _page.GetDeleteDetailsAsync(_account!, _item.Codespace.Name, _token).ConfigureAwait(false);
            lock (_lock)
            {
                if (_finished || !_page.CanDelete(_item, _account, _generation))
                {
                    return;
                }

                if (details.Name != _item.Codespace.Name || details.RepositoryFullName != _item.Codespace.RepositoryFullName)
                {
                    _status = "The codespace changed. Return to Codespaces and refresh.";
                    _finished = true;
                }
                else
                {
                    _details = details;
                    _status = $"Branch: {details.Branch ?? "Unknown"}. Review git status before confirming.";
                }
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            return;
        }
        catch (GitHubApiException ex)
        {
            lock (_lock)
            {
                if (_finished)
                {
                    return;
                }

                _status = $"{ex.Message} Git status and safety are unknown. Return to Codespaces and refresh.";
                _finished = true;
            }
        }

        if (_page.IsDeleteContextCurrent(_account, _generation))
        {
            RaiseItemsChanged();
        }
    }

    private async Task DeleteAsync()
    {
        var status = await _page.DeleteAsync(_item, _details!, _account!, _generation).ConfigureAwait(false);
        lock (_lock)
        {
            _status = status;
        }

        if (_page.IsDeleteContextCurrent(_account, _generation))
        {
            RaiseItemsChanged();
        }
    }

    private static string Warning(string name, bool? value) => value switch
    {
        true => $"{name}: WARNING - these changes may be lost.",
        false => $"{name}: none reported.",
        null => $"{name}: unknown - changes may be lost.",
    };

    private sealed partial class DeleteConfirmationCommand : InvokableCommand
    {
        private readonly DeleteCodespacePage _page;
        private readonly bool _cancel;

        internal DeleteConfirmationCommand(DeleteCodespacePage page, bool cancel)
        {
            _page = page;
            _cancel = cancel;
            Name = cancel ? "Cancel" : "Permanently delete";
        }

        public override ICommandResult Invoke()
        {
            if (_cancel)
            {
                return _page.Cancel();
            }

            _ = _page.ConfirmAsync();
            return CommandResult.KeepOpen();
        }
    }
}
