using System.Collections.Concurrent;

namespace DumbMcpMultiplexer.Services;

/// <summary>
/// Singleton broadcast channel that notifies active MCP sessions when a profile has been
/// saved (servers added/removed, Code Mode toggled, capability overrides changed, etc.).
/// Sessions subscribe for their lifetime; on notification each session sends a
/// tools/list_changed notification so the client re-fetches the updated tool list.
/// </summary>
public sealed class ProfileChangeNotifier
{
    private readonly ConcurrentDictionary<Guid, Func<Task>> _subscribers = new();

    /// <summary>
    /// Registers a callback to be invoked when any profile changes.
    /// Dispose the returned handle to unsubscribe (done automatically when the session ends).
    /// </summary>
    public IDisposable Subscribe(Func<Task> callback)
    {
        var id = Guid.NewGuid();
        _subscribers[id] = callback;
        return new Subscription(() => _subscribers.TryRemove(id, out _));
    }

    /// <summary>
    /// Fires all registered callbacks. Called by ProfileService after saving a profile.
    /// Each callback is invoked on the thread-pool so a slow or disconnected session
    /// cannot block the save operation.
    /// </summary>
    public void Notify()
    {
        foreach (var callback in _subscribers.Values)
        {
            _ = Task.Run(async () =>
            {
                try { await callback(); }
                catch { /* session may have already disconnected */ }
            });
        }
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
