using System.Text.Json;
using Domain.Contracts;
using Domain.Extensions;
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

    protected override async ValueTask<IEnumerable<ChatMessage>> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return await store.GetMessagesAsync(key) ?? [];
    }

    protected override async ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // ToChatResponse does not preserve AdditionalProperties on ChatMessage objects,
        // so response messages arrive without timestamps. Stamp them before persisting.
        var now = DateTimeOffset.UtcNow;
        foreach (var message in context.ResponseMessages?.Where(x => x.GetTimestamp() is null) ?? [])
        {
            message.SetTimestamp(now);
        }

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