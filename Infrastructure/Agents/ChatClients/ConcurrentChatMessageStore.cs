using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class ConcurrentChatMessageStore : ChatMessageStore
{
    private readonly Lock _lock = new();
    private ImmutableList<ChatMessage> _messages = [];

    public ConcurrentChatMessageStore() { }

    public override Task<IEnumerable<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<ChatMessage>>(_messages);
    }

    public override Task AddMessagesAsync(
        IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        lock (_lock)
        {
            _messages = _messages.AddRange(messages);
        }

        return Task.CompletedTask;
    }

    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        var state = new StoreState { Messages = [.. _messages] };
        return JsonSerializer.SerializeToElement(state, jsonSerializerOptions);
    }

    public ConcurrentChatMessageStore(
        JsonElement serializedStoreState, JsonSerializerOptions? jsonSerializerOptions = null)
    {
        if (serializedStoreState.ValueKind is not JsonValueKind.Object)
        {
            return;
        }

        var state = serializedStoreState.Deserialize<StoreState>(jsonSerializerOptions);
        if (state?.Messages is { } messages)
        {
            _messages = [.. messages];
        }
    }

    private sealed class StoreState
    {
        public List<ChatMessage> Messages { get; init; } = [];
    }
}