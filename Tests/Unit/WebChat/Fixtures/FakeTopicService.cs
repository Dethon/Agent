using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Fixtures;

public sealed class FakeTopicService : ITopicService
{
    private readonly Dictionary<(long ChatId, long ThreadId), List<ChatHistoryMessage>> _history = new();
    private readonly List<TopicMetadata> _savedTopics = new();
    private readonly HashSet<string> _deletedTopicIds = new();

    public void SetHistory(long chatId, long threadId, params ChatHistoryMessage[] messages)
    {
        _history[(chatId, threadId)] = messages.ToList();
    }

    public void SetHistory(long chatId, long threadId, List<ChatHistoryMessage> messages)
    {
        _history[(chatId, threadId)] = messages;
    }

    public IReadOnlyList<TopicMetadata> SavedTopics => _savedTopics;
    public IReadOnlySet<string> DeletedTopicIds => _deletedTopicIds;

    public Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId)
    {
        return Task.FromResult<IReadOnlyList<TopicMetadata>>(
            _savedTopics.Where(t => t.AgentId == agentId).ToList());
    }

    public Task SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        _savedTopics.Add(topic);
        return Task.CompletedTask;
    }

    public Task DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId)
    {
        _deletedTopicIds.Add(topicId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(long chatId, long threadId)
    {
        return Task.FromResult<IReadOnlyList<ChatHistoryMessage>>(
            _history.TryGetValue((chatId, threadId), out var h) ? h : []);
    }
}