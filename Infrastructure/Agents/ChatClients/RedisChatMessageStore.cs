using System.Text.Json;
using Domain.Contracts;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

public sealed class RedisChatMessageStore(IThreadStateStore store, string key) : ChatHistoryProvider
{
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static ValueTask<ChatHistoryProvider> Create(
        IThreadStateStore store,
        ChatClientAgentOptions.ChatHistoryProviderFactoryContext ctx,
        CancellationToken ct = default)
    {
        var redisKey = ResolveRedisKey(ctx);
        var chatStore = new RedisChatMessageStore(store, redisKey);
        return ValueTask.FromResult<ChatHistoryProvider>(chatStore);
    }

    private static string ResolveRedisKey(ChatClientAgentOptions.ChatHistoryProviderFactoryContext ctx)
    {
        if (ctx.SerializedState.ValueKind == JsonValueKind.String)
        {
            return ctx.SerializedState.GetString() ?? Guid.NewGuid().ToString();
        }

        return Guid.NewGuid().ToString();
    }

    public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return await store.GetMessagesAsync(key) ?? [];
    }

    public override async ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var existingMessages = await store.GetMessagesAsync(key) ?? [];
        var allMessages = existingMessages
            .Concat(context.RequestMessages)
            .Concat(context.ResponseMessages ?? [])
            .ToArray();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await store.SetMessagesAsync(key, allMessages);
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
}