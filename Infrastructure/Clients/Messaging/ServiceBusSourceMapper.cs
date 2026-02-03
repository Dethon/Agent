using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusSourceMapper(
    IConnectionMultiplexer redis,
    IThreadStateStore threadStateStore,
    ILogger<ServiceBusSourceMapper> logger)
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<(long ChatId, long ThreadId, string TopicId, bool IsNew)> GetOrCreateMappingAsync(
        string sourceId,
        string agentId,
        CancellationToken ct = default)
    {
        var redisKey = $"sb-source:{agentId}:{sourceId}";
        var existingJson = await _db.StringGetAsync(redisKey);

        if (existingJson.HasValue)
        {
            var existing = JsonSerializer.Deserialize<SourceMapping>(existingJson.ToString());
            if (existing is not null)
            {
                logger.LogDebug(
                    "Found existing mapping for sourceId={SourceId}: chatId={ChatId}, threadId={ThreadId}",
                    sourceId, existing.ChatId, existing.ThreadId);
                return (existing.ChatId, existing.ThreadId, existing.TopicId, false);
            }
        }

        var topicId = TopicIdHasher.GenerateTopicId();
        var chatId = TopicIdHasher.GetChatIdForTopic(topicId);
        var threadId = TopicIdHasher.GetThreadIdForTopic(topicId);
        var topicName = $"[SB] {sourceId}";

        var topic = new TopicMetadata(
            TopicId: topicId,
            ChatId: chatId,
            ThreadId: threadId,
            AgentId: agentId,
            Name: topicName,
            CreatedAt: DateTimeOffset.UtcNow,
            LastMessageAt: null);

        await threadStateStore.SaveTopicAsync(topic);

        var mapping = new SourceMapping(chatId, threadId, topicId);
        var mappingJson = JsonSerializer.Serialize(mapping);
        await _db.StringSetAsync(redisKey, mappingJson, TimeSpan.FromDays(30), false);

        logger.LogInformation(
            "Created new mapping for sourceId={SourceId}: chatId={ChatId}, threadId={ThreadId}, topicId={TopicId}",
            sourceId, chatId, threadId, topicId);

        return (chatId, threadId, topicId, true);
    }

    private sealed record SourceMapping(long ChatId, long ThreadId, string TopicId);
}