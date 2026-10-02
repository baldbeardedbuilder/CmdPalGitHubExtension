// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Pages;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class CreateCodespaceCommand : InvokableCommand
{
    private readonly CreateCodespacePage _page;

    public CreateCodespaceCommand(CreateCodespacePage page)
    {
        _page = page;
        Name = "Create Codespace";
        Icon = Icons.Codespaces;
    }

    public override ICommandResult Invoke() => _page.Open();
}
