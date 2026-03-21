using System.Collections.Concurrent;

namespace Infrastructure.Clients.Messaging.WebChat;

public sealed class WebChatSessionManager
{
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();
    private readonly ConcurrentDictionary<long, string> _chatToTopic = new();
    private readonly ConcurrentDictionary<string, string> _conversationToTopic = new();

    public bool StartSession(string topicId, string agentId, long chatId, long threadId, string? spaceSlug = null)
    {
        var session = new WebChatSession(agentId, chatId, threadId, spaceSlug);
        _sessions[topicId] = session;
        _chatToTopic[chatId] = topicId;
        _conversationToTopic[$"{chatId}:{threadId}"] = topicId;
        return true;
    }

    public bool TryGetSession(string topicId, out WebChatSession? session)
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
}