using System.Collections.Immutable;
using Domain.Contracts;
using Infrastructure.Storage;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents;

public class PersistentConversationHistory
{
    private readonly List<ChatMessage> _messages;
    private readonly Lock _lock = new();
    private readonly IConversationHistoryStore _store;
    private readonly string _conversationId;

    private PersistentConversationHistory(
        string conversationId,
        IEnumerable<ChatMessage> initialMessages,
        IConversationHistoryStore store)
    {
        _conversationId = conversationId;
        _messages = initialMessages.ToList();
        _store = store;
    }

    public static async Task<PersistentConversationHistory> LoadOrCreateAsync(
        string conversationId,
        IEnumerable<ChatMessage> defaultMessages,
        IConversationHistoryStore store,
        CancellationToken ct)
    {
        var data = await store.GetAsync(conversationId, ct);
        var messages = data is not null
            ? ChatMessageSerializer.Deserialize(data)
            : defaultMessages;

        return new PersistentConversationHistory(conversationId, messages, store);
    }

    public ImmutableList<ChatMessage> GetSnapshot()
    {
        lock (_lock)
        {
            return [.._messages];
        }
    }

    public async Task AddMessagesAsync(IEnumerable<ChatMessage> messages, CancellationToken ct)
    {
        lock (_lock)
        {
            _messages.AddRange(messages);
        }

        await PersistAsync(ct);
    }

    public async Task AddMessagesAsync(ChatResponse response, CancellationToken ct)
    {
        lock (_lock)
        {
            _messages.AddMessages(response);
        }

        await PersistAsync(ct);
    }

    public async Task AddMessagesAsync(IEnumerable<SamplingMessage>? messages, CancellationToken ct)
    {
        var chatMessages = messages?
            .Select(x => new ChatMessage(
                x.Role == Role.Assistant ? ChatRole.Assistant : ChatRole.User,
                x.Content.ToAIContents()));
        lock (_lock)
        {
            _messages.AddRange(chatMessages ?? []);
        }

        await PersistAsync(ct);
    }

    public async Task AddOrChangeSystemPromptAsync(string? prompt, CancellationToken ct)
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

        await PersistAsync(ct);
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        byte[] data;
        lock (_lock)
        {
            data = ChatMessageSerializer.Serialize(_messages);
        }

        await _store.SaveAsync(_conversationId, data, ct);
    }
}