using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Fixtures;

public sealed class FakeTopicService : ITopicService
{
    private readonly HashSet<string> _deletedTopicIds = new();
    private readonly Dictionary<(long ChatId, long ThreadId), List<ChatHistoryMessage>> _history = new();
    private readonly List<TopicMetadata> _savedTopics = new();

    public IReadOnlyList<TopicMetadata> SavedTopics => _savedTopics;
    public IReadOnlySet<string> DeletedTopicIds => _deletedTopicIds;

    public Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync()
    {
        return Task.FromResult<IReadOnlyList<TopicMetadata>>(_savedTopics);
    }

    public Task SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        _savedTopics.Add(topic);
        return Task.CompletedTask;
    }

    public Task DeleteTopicAsync(string topicId, long chatId, long threadId)
    {
        _deletedTopicIds.Add(topicId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(long chatId, long threadId)
    {
        return Task.FromResult<IReadOnlyList<ChatHistoryMessage>>(
            _history.TryGetValue((chatId, threadId), out var h) ? h : []);
    }

    public void SetHistory(long chatId, long threadId, params ChatHistoryMessage[] messages)
    {
        _history[(chatId, threadId)] = messages.ToList();
    }

    public void SetHistory(long chatId, long threadId, List<ChatHistoryMessage> messages)
    {
        _history[(chatId, threadId)] = messages;
    }
}