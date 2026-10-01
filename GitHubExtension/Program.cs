// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Shmuelie.WinRTServer.CsWinRT;

namespace BaldBeardedBuilder.CmdPal.GitHub;

public static class Program
{
    [MTAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
        {
            Console.WriteLine("Not being launched as an extension. Exiting.");
            return;
        }

        using ManualResetEvent extensionDisposedEvent = new(false);
        var server = new Shmuelie.WinRTServer.ComServer();

        try
        {
            // One extension instance for the life of the process. Command Palette gets the same object every time it asks.
            var extension = new GitHubExtension(extensionDisposedEvent);
            server.RegisterClass<GitHubExtension, IExtension>(() => extension);
            server.Start();

            extensionDisposedEvent.WaitOne();
            server.Stop();
        }
        finally
        {
            server.UnsafeDispose();
        }
    }
}
