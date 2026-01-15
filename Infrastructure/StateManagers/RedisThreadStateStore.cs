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

    public ChatMessage[]? GetMessages(string key)
    {
        var value = _db.StringGet(key);
        return value.HasValue
            ? JsonSerializer.Deserialize<StoreState>(value.ToString())?.Messages
            : null;
    }

    public async Task SetMessagesAsync(string key, ChatMessage[] messages)
    {
        var json = JsonSerializer.Serialize(new StoreState { Messages = messages });
        await _db.StringSetAsync(key, json, expiration);
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync()
    {
        var topics = new List<TopicMetadata>();

        await foreach (var key in _server.KeysAsync(pattern: "topic:*"))
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

    public async Task DeleteTopicAsync(string topicId)
    {
        await _db.KeyDeleteAsync(TopicKey(topicId));
    }

    private static string TopicKey(string topicId)
    {
        return $"topic:{topicId}";
    }

    private sealed class StoreState
    {
        public ChatMessage[] Messages { get; init; } = [];
    }
}