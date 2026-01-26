using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisThreadStateStore(IConnectionMultiplexer redis, TimeSpan expiration)
    : IThreadStateStore
{
    private readonly IDatabase _db = redis.GetDatabase();
    private readonly IServer _server = redis.GetServer(redis.GetEndPoints()[0]);

    public async Task DeleteAsync(AgentKey key)
    {
        await _db.KeyDeleteAsync(key.ToString());
    }

    public async Task<ChatMessage[]?> GetMessagesAsync(string key)
    {
        var value = await _db.StringGetAsync(key);
        return value.HasValue
            ? JsonSerializer.Deserialize<StoreState>(value.ToString())?.Messages
            : null;
    }

    public async Task SetMessagesAsync(string key, ChatMessage[] messages)
    {
        var json = JsonSerializer.Serialize(new StoreState { Messages = messages });
        await _db.StringSetAsync(key, json, expiration);
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId)
    {
        var topics = new List<TopicMetadata>();

        await foreach (var key in _server.KeysAsync(pattern: $"topic:{agentId}:*"))
        {
            var json = await _db.StringGetAsync(key);
            if (json.IsNullOrEmpty)
            {
                continue;
            }

            var topic = JsonSerializer.Deserialize<TopicMetadata>(json.ToString());
            if (topic is not null)
            {
                topics.Add(topic);
            }
        }

        return topics.OrderByDescending(t => t.LastMessageAt ?? t.CreatedAt).ToList();
    }

    public async Task SaveTopicAsync(TopicMetadata topic)
    {
        var json = JsonSerializer.Serialize(topic);
        await _db.StringSetAsync(TopicKey(topic.AgentId, topic.ChatId, topic.TopicId), json, expiration);
    }

    public async Task DeleteTopicAsync(string agentId, long chatId, string topicId)
    {
        await _db.KeyDeleteAsync(TopicKey(agentId, chatId, topicId));
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        return await _db.KeyExistsAsync(key);
    }

    public async Task<TopicMetadata?> GetTopicByChatIdAndThreadIdAsync(
        string agentId, long chatId, long threadId, CancellationToken ct = default)
    {
        var topics = await GetAllTopicsAsync(agentId);
        return topics.FirstOrDefault(t => t.ChatId == chatId && t.ThreadId == threadId);
    }

    private static string TopicKey(string agentId, long chatId, string topicId)
    {
        return $"topic:{agentId}:{chatId}:{topicId}";
    }

    private sealed class StoreState
    {
        public ChatMessage[] Messages { get; init; } = [];
    }
}