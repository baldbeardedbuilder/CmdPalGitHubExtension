// Copyright (c) Bald Bearded Builder LLC
// Bald Bearded Builder LLC licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace BaldBeardedBuilder.CmdPal.GitHub.Auth;

internal interface IAccountSubscription : IDisposable
{
    bool IsAlive { get; }

    void Notify();
}

internal sealed partial class AccountSubscription<T>(AuthService auth, T target, Action<T> changed) : IAccountSubscription
    where T : class
{
    private readonly WeakReference<T> _target = new(target);
    private int _disposed;

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && _target.TryGetTarget(out _);

    public void Notify()
    {
        if (Volatile.Read(ref _disposed) == 0 && _target.TryGetTarget(out var page))
        {
            changed(page);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            auth.Unsubscribe(this);
        }
    }
}
