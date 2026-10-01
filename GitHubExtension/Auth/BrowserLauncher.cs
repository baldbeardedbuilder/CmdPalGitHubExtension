// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal interface IBrowserLauncher
{
    void Open(Uri uri);
}

internal sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
}
