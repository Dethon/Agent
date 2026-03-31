using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

public sealed class SessionService : ISessionService
{
    private readonly ConcurrentDictionary<string, ChannelSession> _sessions = new();
    private readonly ConcurrentDictionary<long, string> _chatToTopic = new();
    private readonly ConcurrentDictionary<string, string> _conversationToTopic = new();

    public Task<string> CreateConversationAsync(CreateConversationParams p)
    {
        var agentId = p.AgentId;
        var topicId = Guid.NewGuid().ToString("N");
        var chatId = GetDeterministicHash(topicId, seed: 0x1234);
        var threadId = GetDeterministicHash(topicId, seed: 0x5678) & 0x7FFFFFFF;

        StartSession(topicId, agentId, chatId, threadId, spaceSlug: "default", topicName: p.TopicName);

        // Return conversationId in "{chatId}:{threadId}" format — this is what the agent
        // uses as AgentKey and passes back to send_reply/request_approval
        var conversationId = $"{chatId}:{threadId}";
        return Task.FromResult(conversationId);
    }

    public bool StartSession(string topicId, string agentId, long chatId, long threadId, string? spaceSlug = null, string? topicName = null)
    {
        var session = new ChannelSession(agentId, chatId, threadId, spaceSlug, topicName);
        _sessions[topicId] = session;
        _chatToTopic[chatId] = topicId;
        _conversationToTopic[$"{chatId}:{threadId}"] = topicId;
        return true;
    }

    public bool TryGetSession(string topicId, out ChannelSession? session)
    {
        return _sessions.TryGetValue(topicId, out session);
    }

    public void EndSession(string topicId)
    {
        if (!_sessions.TryRemove(topicId, out var session))
        {
            return;
        }

        _chatToTopic.TryRemove(session.ChatId, out _);
        _conversationToTopic.TryRemove($"{session.ChatId}:{session.ThreadId}", out _);
    }

    public string? GetTopicIdByChatId(long chatId)
    {
        return _chatToTopic.GetValueOrDefault(chatId);
    }

    public string? GetTopicIdByConversationId(string conversationId)
    {
        return _conversationToTopic.GetValueOrDefault(conversationId);
    }

    public ChannelSession? GetSessionByConversationId(string conversationId)
    {
        var topicId = GetTopicIdByConversationId(conversationId);
        return topicId is not null && _sessions.TryGetValue(topicId, out var session) ? session : null;
    }

    private static long GetDeterministicHash(string input, long seed)
    {
        const long fnvPrime = 0x100000001b3;
        var hash = unchecked((long)0xcbf29ce484222325) ^ seed;

        foreach (var c in input)
        {
            hash ^= c;
            hash = unchecked(hash * fnvPrime);
        }

        return hash & 0x7FFFFFFFFFFFFFFF;
    }
}