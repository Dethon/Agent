using System.Collections.Immutable;
using System.Text.Json;
using Domain.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.Agents.ChatClients;

public sealed class RedisChatMessageStore(IDatabase db, string key) : ChatMessageStore
{
    private static readonly TimeSpan _expiry = TimeSpan.FromDays(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private ImmutableList<ChatMessage> _messages = [];

    public static string GetRedisKey(AgentKey key)
    {
        return $"thread:{key.ChatId}:{key.ThreadId}";
    }

    public static RedisChatMessageStore Create(
        IDatabase db, ChatClientAgentOptions.ChatMessageStoreFactoryContext ctx)
    {
        var state = ctx.SerializedState.ValueKind == JsonValueKind.Undefined
            ? null
            : ctx.SerializedState.Deserialize<AgentKey?>(ctx.JsonSerializerOptions);

        var agentKey = state is null
            ? Guid.NewGuid().ToString()
            : GetRedisKey(state.Value);

        var store = new RedisChatMessageStore(db, agentKey);
        store.LoadFromRedis();
        return store;
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
            await PersistToRedisAsync();
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

    private void LoadFromRedis()
    {
        var value = db.StringGet(key);
        if (!value.HasValue)
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<StoreState>(value.ToString());
            if (state?.Messages is { } messages)
            {
                _messages = [.. messages];
            }
        }
        catch (JsonException)
        {
            _messages = [];
        }
    }

    private async Task PersistToRedisAsync()
    {
        var state = new StoreState { Messages = [.. _messages] };
        var json = JsonSerializer.Serialize(state);
        await db.StringSetAsync(key, json, _expiry);
    }

    private sealed class StoreState
    {
        public List<ChatMessage> Messages { get; init; } = [];
    }
}