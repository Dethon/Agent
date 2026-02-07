using System.Collections.Concurrent;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusConversationMapper(
    IConnectionMultiplexer redis,
    IThreadStateStore threadStateStore,
    ILogger<ServiceBusConversationMapper> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly ConcurrentDictionary<long, string> _chatIdToCorrelationId = new();

    public async Task<(long ChatId, long ThreadId, string TopicId, bool IsNew)> GetOrCreateMappingAsync(
        string correlationId,
        string agentId,
        CancellationToken ct = default)
    {
        var redisKey = $"sb-correlation:{agentId}:{correlationId}";
        var existingJson = await _db.StringGetAsync(redisKey);

        if (existingJson.HasValue)
        {
            var existing = JsonSerializer.Deserialize<CorrelationMapping>(existingJson.ToString());
            if (existing is not null)
            {
                logger.LogDebug(
                    "Found existing mapping for correlationId={CorrelationId}: chatId={ChatId}, threadId={ThreadId}",
                    correlationId, existing.ChatId, existing.ThreadId);

                // Refresh topic TTL to prevent expiration before correlation mapping
                await threadStateStore.SaveTopicAsync(new TopicMetadata(
                    TopicId: existing.TopicId,
                    ChatId: existing.ChatId,
                    ThreadId: existing.ThreadId,
                    AgentId: agentId,
                    Name: $"[SB] {correlationId}",
                    CreatedAt: DateTimeOffset.UtcNow,
                    LastMessageAt: DateTimeOffset.UtcNow));

                _chatIdToCorrelationId[existing.ChatId] = correlationId;
                return (existing.ChatId, existing.ThreadId, existing.TopicId, false);
            }
        }

        var topicId = TopicIdHasher.GenerateTopicId();
        var chatId = TopicIdHasher.GetChatIdForTopic(topicId);
        var threadId = TopicIdHasher.GetThreadIdForTopic(topicId);
        var topicName = $"[SB] {correlationId}";

        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: agentId,
            Name: topicName,
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        await threadStateStore.SaveTopicAsync(topic);

        var mapping = new CorrelationMapping(chatId, threadId, topicId);
        var mappingJson = JsonSerializer.Serialize(mapping);
        await _db.StringSetAsync(redisKey, mappingJson, TimeSpan.FromDays(30), false);

        _chatIdToCorrelationId[chatId] = correlationId;

        logger.LogInformation(
            "Created new mapping for correlationId={CorrelationId}: chatId={ChatId}, threadId={ThreadId}, topicId={TopicId}",
            correlationId, chatId, threadId, topicId);

        return (chatId, threadId, topicId, true);
    }

    public bool TryGetCorrelationId(long chatId, out string correlationId)
    {
        return _chatIdToCorrelationId.TryGetValue(chatId, out correlationId!);
    }

    private sealed record CorrelationMapping(long ChatId, long ThreadId, string TopicId);
}