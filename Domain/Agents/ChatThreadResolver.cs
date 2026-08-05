using System.Collections.Concurrent;
using Domain.Contracts;

namespace Domain.Agents;

public sealed class ChatThreadResolver(IThreadStateStore? threadStateStore = null) : IDisposable
{
    private readonly ConcurrentDictionary<AgentKey, ChatThreadContext> _contexts = [];
    private readonly Lock _lock = new();
    private int _isDisposed;

    public IEnumerable<AgentKey> AgentKeys => _contexts.Keys;

    public ChatThreadContext Resolve(AgentKey key)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        lock (_lock)
        {
            if (_contexts.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var context = new ChatThreadContext();
            _contexts[key] = context;
            return context;
        }
    }

    public void Cancel(AgentKey key)
    {
        if (_isDisposed != 0)
        {
            return;
        }

        if (_contexts.Remove(key, out var context))
        {
            context.Dispose();
        }
    }

    public async Task ClearAsync(AgentKey key)
    {
        if (_isDisposed != 0)
        {
            return;
        }

        // The delete does not depend on finding a live context. A /cancel arriving just before
        // the /clear already removed and disposed it, and a /clear on a conversation with no live
        // group is routine after a restart; in both cases the user asked for the history to be
        // gone. The store delete is one idempotent key delete, so doing it either way costs a
        // round trip and never leaves the cleared history behind.
        if (_contexts.Remove(key, out var context))
        {
            context.Dispose();
        }

        await DeletePersistedStateAsync(key);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        lock (_lock)
        {
            foreach (var context in _contexts.Values)
            {
                context.Dispose();
            }
        }

        _contexts.Clear();
    }

    private async Task DeletePersistedStateAsync(AgentKey key)
    {
        if (threadStateStore is null)
        {
            return;
        }

        await threadStateStore.DeleteAsync(key);
    }
}