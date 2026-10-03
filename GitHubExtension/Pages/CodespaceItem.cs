// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;
using BaldBeardedBuilder.CmdPal.GitHub.Codespaces;
using BaldBeardedBuilder.CmdPal.GitHub.Commands;

namespace BaldBeardedBuilder.CmdPal.GitHub.Pages;

internal sealed partial class CodespaceItem : ListItem
{
    public CodespaceItem(CodespacesPage page, GitHubCodespace codespace, IBrowserLauncher browser, DateTimeOffset now)
    {
        Codespace = codespace;
        Command = new OpenInBrowserCommand(browser, codespace.WebUrl, "Open", Icons.Codespaces);
        Title = codespace.RepositoryFullName;
        Subtitle = CodespaceFormatting.Subtitle(codespace, now);
        Icon = Icons.Codespaces;
        Tags = [CodespaceFormatting.StateTag(codespace.State)];

        var more = new List<IContextItem>
        {
            new CommandContextItem(new CopyTextCommand(codespace.WebUrl.AbsoluteUri) { Name = "Copy URL", Icon = Icons.Copy }),
            new CommandContextItem(new CopyTextCommand(codespace.Name) { Name = "Copy name", Icon = Icons.Copy }),
            new CommandContextItem(new DeleteCodespacePage(page, this)),
        };
        if (codespace.State == "Available")
        {
            more.Insert(0, new CommandContextItem(new CloseCodespaceCommand(page, this)));
        }
        else if (codespace.State == "Shutdown")
        {
            more.Insert(0, new CommandContextItem(new StartCodespaceCommand(page, this)));
        }

        if (page.CreatePage is { } createPage)
        {
            more.Add(new CommandContextItem(createPage));
        }

        more.Add(new CommandContextItem(new RefreshCodespacesCommand(page)));
        MoreCommands = [.. more];
    }

    public GitHubCodespace Codespace { get; }

    public bool Matches(string[] terms) => terms.All(t =>
        Codespace.RepositoryFullName.Contains(t, StringComparison.OrdinalIgnoreCase)
        || Codespace.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
        || (Codespace.DisplayName?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Codespace.Branch?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
        || Codespace.State.Contains(t, StringComparison.OrdinalIgnoreCase)
        || CodespaceFormatting.StateLabel(Codespace.State).Contains(t, StringComparison.OrdinalIgnoreCase));
}
