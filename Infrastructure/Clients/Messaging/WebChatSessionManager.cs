using System.Collections.Concurrent;

namespace Infrastructure.Clients.Messaging;

public sealed class WebChatSessionManager
{
    private readonly ConcurrentDictionary<long, string> _chatToTopic = new();
    private readonly ConcurrentDictionary<string, WebChatSession> _sessions = new();

    public bool StartSession(string topicId, string agentId, long chatId, long threadId)
    {
        var session = new WebChatSession(agentId, chatId, threadId);
        _sessions[topicId] = session;
        _chatToTopic[chatId] = topicId;
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
    }

    public string? GetTopicIdByChatId(long chatId)
    {
        return _chatToTopic.GetValueOrDefault(chatId);
    }
}