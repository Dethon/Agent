using System.Collections.Concurrent;
using Domain.Contracts;

namespace Domain.Agents;

public sealed class ChatThreadResolver(IThreadStateStore? threadStateStore = null) : IAsyncDisposable
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

    public async Task CleanAsync(AgentKey key)
    {
        if (_isDisposed != 0)
        {
            return;
        }

        if (_contexts.Remove(key, out var context))
        {
            context.Dispose();
            await DeletePersistedStateAsync(key);
        }
    }

    public async ValueTask DisposeAsync()
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

        foreach (var key in _contexts.Keys)
        {
            await DeletePersistedStateAsync(key);
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