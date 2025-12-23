using System.Collections.Immutable;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class RedisChatMessageStore(IThreadStateStore store, string key) : ChatMessageStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ImmutableList<ChatMessage> _messages = [];

    public static RedisChatMessageStore CreateAsync(
        IThreadStateStore store, ChatClientAgentOptions.ChatMessageStoreFactoryContext ctx)
    {
        var redisKey = ResolveRedisKey(ctx);
        var chatStore = new RedisChatMessageStore(store, redisKey);
        chatStore.LoadFromStoreAsync();
        return chatStore;
    }

    private static string ResolveRedisKey(ChatClientAgentOptions.ChatMessageStoreFactoryContext ctx)
    {
        if (ctx.SerializedState.ValueKind == JsonValueKind.String)
        {
            return ctx.SerializedState.GetString() ?? Guid.NewGuid().ToString();
        }

        return Guid.NewGuid().ToString();
    }

    public override Task<IEnumerable<ChatMessage>> GetMessagesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<ChatMessage>>(_messages);
    }

    public override async Task AddMessagesAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            _messages = _messages.AddRange(messages);
            await PersistToStoreAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        return JsonSerializer.SerializeToElement(key, jsonSerializerOptions);
    }

    private void LoadFromStoreAsync()
    {
        var messages = store.GetMessages(key) ?? [];
        _messages = [.. messages];
    }

    private async Task PersistToStoreAsync()
    {
        await store.SetMessagesAsync(key, [.. _messages]);
    }
}