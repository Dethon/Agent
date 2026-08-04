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

    public Exception? ThrowOnGetHistory { get; set; }

    public Exception? ThrowOnDeleteTopic { get; set; }

    public IReadOnlyList<TopicMetadata> SavedTopics => _savedTopics;
    public IReadOnlySet<string> DeletedTopicIds => _deletedTopicIds;
    public IReadOnlyList<string> JoinedSpaces => _joinedSpaces;

    // Set to answer not live for every call, the way a transport between connections does.
    public bool NotLive { get; set; }

    public Task<HubResult<IReadOnlyList<TopicMetadata>>> GetAllTopicsAsync(
        string agentId, string spaceSlug = "default")
    {
        recorder?.Record($"topics:{agentId}");

        if (ThrowOnGetAllTopics is not null)
        {
            return Task.FromException<HubResult<IReadOnlyList<TopicMetadata>>>(ThrowOnGetAllTopics);
        }

        if (NotLive)
        {
            return Task.FromResult(HubResult<IReadOnlyList<TopicMetadata>>.NotLive);
        }

        return Task.FromResult(HubResult<IReadOnlyList<TopicMetadata>>.Answered(
            _seededTopics.Concat(_savedTopics)
                .Where(t => t.AgentId == agentId && t.SpaceSlug == spaceSlug)
                .ToList()));
    }

    public Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug)
    {
        recorder?.Record($"join:{spaceSlug}");

        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _joinedSpaces.Add(spaceSlug);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false)
    {
        recorder?.Record($"save:{topic.TopicId}");

        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _savedTopics.Add(topic);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId)
    {
        recorder?.Record($"delete:{topicId}");

        if (ThrowOnDeleteTopic is not null)
        {
            return Task.FromException<HubResult<Nothing>>(ThrowOnDeleteTopic);
        }

        if (NotLive)
        {
            return Task.FromResult(HubResult<Nothing>.NotLive);
        }

        _deletedTopicIds.Add(topicId);
        return Task.FromResult(HubResult<Nothing>.Answered(default));
    }

    public Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(
        string agentId, long chatId, long threadId)
    {
        recorder?.Record($"history:{chatId}:{threadId}");

        if (ThrowOnGetHistory is not null)
        {
            return Task.FromException<HubResult<IReadOnlyList<ChatHistoryMessage>>>(ThrowOnGetHistory);
        }

        if (NotLive)
        {
            return Task.FromResult(HubResult<IReadOnlyList<ChatHistoryMessage>>.NotLive);
        }

        return Task.FromResult(HubResult<IReadOnlyList<ChatHistoryMessage>>.Answered(
            _history.TryGetValue((chatId, threadId), out var h) ? h : []));
    }
}