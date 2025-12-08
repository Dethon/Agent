using System.Collections.Concurrent;

namespace Domain.Agents;

public class ChatThreadResolver
{
    private readonly ConcurrentDictionary<AgentKey, ChatThreadContext> _contexts = [];
    private readonly Lock _lock = new();

    public IEnumerable<AgentKey> AgentKeys => _contexts.Keys;

    public (ChatThreadContext context, bool isNew) Resolve(AgentKey key)
    {
        lock (_lock)
        {
            if (_contexts.TryGetValue(key, out var existing))
            {
                return (existing, false);
            }

            var context = new ChatThreadContext();
            _contexts[key] = context;
            return (context, true);
        }
    }

    public void Clean(AgentKey key)
    {
        if (_contexts.Remove(key, out var context))
        {
            context.Complete();
        }
    }
}