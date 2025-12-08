using System.Collections.Concurrent;

namespace Domain.Agents;

public class ChatThreadResolver
{
    private readonly ConcurrentDictionary<AgentKey, ChatThreadContext> _contexts = [];

    public IEnumerable<AgentKey> AgentKeys => _contexts.Keys;

    public (ChatThreadContext context, bool isNew) Resolve(AgentKey key)
    {
        var isNew = false;
        var context = _contexts.GetOrAdd(key, _ =>
        {
            isNew = true;
            return new ChatThreadContext();
        });
        return (context, isNew);
    }

    public void Clean(AgentKey key)
    {
        if (_contexts.Remove(key, out var context))
        {
            context.Complete();
        }
    }
}