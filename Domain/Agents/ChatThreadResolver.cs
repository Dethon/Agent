using System.Collections.Concurrent;

namespace Domain.Agents;

public sealed class ChatThreadResolver : IDisposable
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

    public void Clean(AgentKey key)
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

            _contexts.Clear();
        }
    }
}