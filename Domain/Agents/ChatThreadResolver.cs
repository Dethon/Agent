using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.Extensions;

namespace Domain.Agents;

public sealed class ChatThreadResolver(IThreadStateStore store) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<AgentKey, ChatThreadContext> _contexts = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _isDisposed;

    public IEnumerable<AgentKey> AgentKeys => _contexts.Keys;

    public async Task<ChatThreadContext> ResolveAsync(AgentKey key, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        return await _lock.WithLockAsync(async () =>
        {
            if (_contexts.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var context = await ChatThreadContext.CreateAsync(key, store, ct);
            _contexts[key] = context;
            return context;
        }, ct);
    }

    public async Task CleanAsync(AgentKey key)
    {
        if (_isDisposed != 0)
        {
            return;
        }

        if (_contexts.Remove(key, out var context))
        {
            await context.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        await _lock.WithLockAsync(async () =>
        {
            foreach (var context in _contexts.Values)
            {
                await context.DisposeAsync();
            }

            _contexts.Clear();
        });

        _lock.Dispose();
    }
}