using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class ConversationHistory(IEnumerable<ChatMessage> initialMessages)
{
    private readonly List<ChatMessage> _messages = initialMessages.ToList();
    private readonly Lock _lock = new();

    public ImmutableArray<ChatMessage> GetSnapshot()
    {
        lock (_lock)
        {
            return [.._messages];
        }
    }

    public void AddMessages(IEnumerable<ChatMessage> messages)
    {
        lock (_lock)
        {
            _messages.AddRange(messages);
        }
    }

    public void AddMessages(ChatResponse response)
    {
        lock (_lock)
        {
            _messages.AddMessages(response);
        }
    }

    public void AddMessages(ChatResponseUpdate response)
    {
        lock (_lock)
        {
            _messages.AddMessages(response);
        }
    }
}