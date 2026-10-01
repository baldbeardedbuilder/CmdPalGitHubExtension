// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Tests;

internal sealed class InMemoryAccountStore(GitHubAccount? initial = null) : IAccountStore
{
    public GitHubAccount? Account { get; private set; } = initial;

    public GitHubAccount? Load() => Account;

    public void Save(GitHubAccount account) => Account = account;

    public void Clear() => Account = null;
}

/// <summary>
/// Plays the part of the browser: it hits the loopback redirect the way GitHub would after the user approves.
/// </summary>
internal sealed class FakeBrowser(Func<Uri, Uri?> redirectFor) : IBrowserLauncher
{
    public Uri? LastOpened { get; private set; }

    public void Open(Uri uri)
    {
        LastOpened = uri;
        if (redirectFor(uri) is { } redirect)
        {
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetAsync(redirect);
            });
        }
    }

    public static FakeBrowser Approving(string code = "the-code") => new(uri =>
    {
        var values = System.Web.HttpUtility.ParseQueryString(uri.Query);
        return new Uri($"{values["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(values["state"]!)}");
    });
}
