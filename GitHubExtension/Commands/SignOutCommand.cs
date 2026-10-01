// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using BaldBeardedBuilder.CmdPal.GitHub.Auth;

namespace BaldBeardedBuilder.CmdPal.GitHub.Commands;

internal sealed partial class SignOutCommand : InvokableCommand
{
    private readonly AuthService _auth;

    public SignOutCommand(AuthService auth)
    {
        _auth = auth;
        Name = "Sign out";
        Icon = Icons.SignOut;
    }

    public override ICommandResult Invoke()
    {
        _auth.SignOut();
        return CommandResult.ShowToast(new ToastArgs { Message = "Signed out of GitHub", Result = CommandResult.GoHome() });
    }
}
