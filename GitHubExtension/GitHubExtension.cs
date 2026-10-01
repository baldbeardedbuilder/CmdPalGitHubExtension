// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub;

[Guid("9c935601-0f12-40e1-b22e-d45ec673ef09")]
public sealed partial class GitHubExtension : IExtension, IDisposable
{
    private readonly ManualResetEvent _extensionDisposedEvent;
    private readonly GitHubCommandsProvider _provider = new();
    private readonly Lock _disposeLock = new();
    private bool _isDisposed;

    public GitHubExtension(ManualResetEvent extensionDisposedEvent)
    {
        _extensionDisposedEvent = extensionDisposedEvent;
    }

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        _provider.Dispose();
        _extensionDisposedEvent.Set();
    }
}
