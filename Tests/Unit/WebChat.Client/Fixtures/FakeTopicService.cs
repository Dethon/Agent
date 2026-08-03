using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeTopicService(CallRecorder? recorder = null) : ITopicService
{
    private readonly Dictionary<(long ChatId, long ThreadId), List<ChatHistoryMessage>> _history = new();
    private readonly List<TopicMetadata> _seededTopics = new();
    private readonly List<TopicMetadata> _savedTopics = new();
    private readonly HashSet<string> _deletedTopicIds = new();
    private readonly List<string> _joinedSpaces = new();

    public void SetHistory(long chatId, long threadId, params ChatHistoryMessage[] messages)
    {
        _history[(chatId, threadId)] = messages.ToList();
    }

    public void SetHistory(long chatId, long threadId, List<ChatHistoryMessage> messages)
    {
        _history[(chatId, threadId)] = messages;
    }

    // Topics the server already has. Kept apart from SavedTopics so a test can still assert
    // on what the code under test wrote.
    public FakeTopicService SeedTopic(TopicMetadata topic)
    {
        _seededTopics.Add(topic);
        return this;
    }

    public Exception? ThrowOnGetAllTopics { get; set; }

    public IReadOnlyList<TopicMetadata> SavedTopics => _savedTopics;
    public IReadOnlySet<string> DeletedTopicIds => _deletedTopicIds;
    public IReadOnlyList<string> JoinedSpaces => _joinedSpaces;

    public Task<IReadOnlyList<TopicMetadata>> GetAllTopicsAsync(string agentId, string spaceSlug = "default")
    {
        recorder?.Record($"topics:{agentId}");

        if (ThrowOnGetAllTopics is not null)
        {
            return Task.FromException<IReadOnlyList<TopicMetadata>>(ThrowOnGetAllTopics);
        }

        return Task.FromResult<IReadOnlyList<TopicMetadata>>(
            _seededTopics.Concat(_savedTopics)
                .Where(t => t.AgentId == agentId && t.SpaceSlug == spaceSlug)
                .ToList());
    }

    public Task JoinSpaceAsync(string spaceSlug)
    {
        _joinedSpaces.Add(spaceSlug);
        recorder?.Record($"join:{spaceSlug}");
        return Task.CompletedTask;
    }

    public Task SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        _savedTopics.Add(topic);
        recorder?.Record($"save:{topic.TopicId}");
        return Task.CompletedTask;
    }

    public Task DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId)
    {
        _deletedTopicIds.Add(topicId);
        recorder?.Record($"delete:{topicId}");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatHistoryMessage>> GetHistoryAsync(string agentId, long chatId, long threadId)
    {
        recorder?.Record($"history:{chatId}:{threadId}");

        return Task.FromResult<IReadOnlyList<ChatHistoryMessage>>(
            _history.TryGetValue((chatId, threadId), out var h) ? h : []);
    }
}