using System.Collections.Immutable;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

public class ConversationHistory(IEnumerable<ChatMessage> initialMessages)
{
    private readonly List<ChatMessage> _messages = initialMessages.ToList();
    private readonly Lock _lock = new();

    public ImmutableList<ChatMessage> GetSnapshot()
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

    public void AddMessages(IEnumerable<SamplingMessage>? messages)
    {
        var chatMessages = messages?
            .Select(x => new ChatMessage(
                x.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User,
                x.Content.ToAIContents()));
        lock (_lock)
        {
            _messages.AddRange(chatMessages ?? []);
        }
    }

    public void AddOrChangeSystemPrompt(string? prompt)
    {
        if (prompt is null)
        {
            return;
        }

        lock (_lock)
        {
            var systemMessage = _messages.FirstOrDefault(m => m.Role == ChatRole.System);
            if (systemMessage != null)
            {
                systemMessage.Contents = [new TextContent(prompt)];
            }
            else
            {
                _messages.Insert(0, new ChatMessage(ChatRole.System, [new TextContent(prompt)]));
            }
        }
    }
}