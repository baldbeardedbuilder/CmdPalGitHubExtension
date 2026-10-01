// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal sealed record GitHubAccount(GitHubHost Host, string Login, string Token)
{
    public override string ToString() => $"{Login}@{Host.Name}";
}
