using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;

namespace WebChat.Client.Services.State;

public sealed class ChatStateManager : IChatStateManager
{
    private readonly Dictionary<string, List<ChatMessageModel>> _messagesByTopic = new();
    private readonly Dictionary<string, ChatMessageModel?> _streamingMessageByTopic = new();
    private readonly HashSet<string> _streamingTopics = new();
    private readonly HashSet<string> _resumingTopics = new();
    private readonly Dictionary<string, int> _lastSeenMessageCountByTopic = new();
    private readonly List<StoredTopic> _topics = [];
    private readonly List<AgentInfo> _agents = [];

    public IReadOnlyList<StoredTopic> Topics => _topics;
    public StoredTopic? SelectedTopic { get; private set; }

    public string? SelectedAgentId { get; private set; }

    public IReadOnlyList<AgentInfo> Agents => _agents;
    public ToolApprovalRequestMessage? CurrentApprovalRequest { get; private set; }

    public IReadOnlySet<string> StreamingTopics => _streamingTopics;

    public IReadOnlyList<ChatMessageModel> CurrentMessages =>
        SelectedTopic is not null && _messagesByTopic.TryGetValue(SelectedTopic.TopicId, out var msgs)
            ? msgs
            : [];

    public ChatMessageModel? CurrentStreamingMessage =>
        SelectedTopic is not null && _streamingMessageByTopic.TryGetValue(SelectedTopic.TopicId, out var msg)
            ? msg
            : null;

    public bool IsCurrentTopicStreaming =>
        SelectedTopic is not null && _streamingTopics.Contains(SelectedTopic.TopicId);

    public IReadOnlyDictionary<string, int> UnreadCounts =>
        _messagesByTopic
            .Where(kvp => kvp.Key != SelectedTopic?.TopicId)
            .Select(kvp => (kvp.Key, AssistantCount: GetAssistantMessageCount(kvp.Key),
                LastRead: GetLastReadCount(kvp.Key)))
            .Where(t => t.AssistantCount > t.LastRead)
            .ToDictionary(t => t.Key, t => t.AssistantCount - t.LastRead);

    public event Action? OnStateChanged;

    public void SetAgents(IReadOnlyList<AgentInfo> agents)
    {
        _agents.Clear();
        _agents.AddRange(agents);
        NotifyStateChanged();
    }

    public void SelectAgent(string agentId)
    {
        if (SelectedAgentId == agentId)
        {
            return;
        }

        SelectedAgentId = agentId;
        SelectedTopic = null;
        NotifyStateChanged();
    }

    public void SelectTopic(StoredTopic? topic)
    {
        SelectedTopic = topic;
        if (topic is not null)
        {
            SelectedAgentId = topic.AgentId;
        }

        NotifyStateChanged();
    }

    public void AddTopic(StoredTopic topic)
    {
        if (_topics.All(t => t.TopicId != topic.TopicId))
        {
            _topics.Add(topic);
            NotifyStateChanged();
        }
    }

    public void RemoveTopic(string topicId)
    {
        _topics.RemoveAll(t => t.TopicId == topicId);
        _messagesByTopic.Remove(topicId);
        _streamingMessageByTopic.Remove(topicId);
        _streamingTopics.Remove(topicId);
        _resumingTopics.Remove(topicId);
        _lastSeenMessageCountByTopic.Remove(topicId);

        if (SelectedTopic?.TopicId == topicId)
        {
            SelectedTopic = null;
            CurrentApprovalRequest = null;
        }

        NotifyStateChanged();
    }

    public void UpdateTopic(TopicMetadata metadata)
    {
        var existingTopic = _topics.FirstOrDefault(t => t.TopicId == metadata.TopicId);
        if (existingTopic is not null)
        {
            existingTopic.Name = metadata.Name;
            existingTopic.LastMessageAt = metadata.LastMessageAt?.DateTime;
            existingTopic.LastReadMessageCount = metadata.LastReadMessageCount;
            NotifyStateChanged();
        }
    }

    public StoredTopic? GetTopicById(string topicId)
    {
        return _topics.FirstOrDefault(t => t.TopicId == topicId);
    }

    public List<ChatMessageModel> GetMessagesForTopic(string topicId)
    {
        if (!_messagesByTopic.TryGetValue(topicId, out var messages))
        {
            messages = [];
            _messagesByTopic[topicId] = messages;
        }

        return messages;
    }

    public void SetMessagesForTopic(string topicId, List<ChatMessageModel> messages)
    {
        _messagesByTopic[topicId] = messages;
        NotifyStateChanged();
    }

    public bool HasMessagesForTopic(string topicId)
    {
        return _messagesByTopic.ContainsKey(topicId);
    }

    public void AddMessage(string topicId, ChatMessageModel message)
    {
        var messages = GetMessagesForTopic(topicId);
        messages.Add(message);
        NotifyStateChanged();
    }

    public void UpdateStreamingMessage(string topicId, ChatMessageModel? message)
    {
        _streamingMessageByTopic[topicId] = message;
        // Note: NotifyStateChanged is NOT called here to allow throttled rendering
    }

    public ChatMessageModel? GetStreamingMessageForTopic(string topicId)
    {
        return _streamingMessageByTopic.GetValueOrDefault(topicId);
    }

    public void StartStreaming(string topicId)
    {
        _streamingTopics.Add(topicId);
        _streamingMessageByTopic[topicId] = new ChatMessageModel { Role = "assistant" };
        NotifyStateChanged();
    }

    public void StopStreaming(string topicId)
    {
        _streamingTopics.Remove(topicId);
        _streamingMessageByTopic.Remove(topicId);
        NotifyStateChanged();
    }

    public bool IsTopicStreaming(string topicId)
    {
        return _streamingTopics.Contains(topicId);
    }

    public bool IsTopicResuming(string topicId)
    {
        return _resumingTopics.Contains(topicId);
    }

    public bool TryStartResuming(string topicId)
    {
        return _resumingTopics.Add(topicId);
    }

    public void StopResuming(string topicId)
    {
        _resumingTopics.Remove(topicId);
    }

    public int GetAssistantMessageCount(string topicId)
    {
        var count = _messagesByTopic.GetValueOrDefault(topicId)?.Count(m => m.Role != "user") ?? 0;

        if (_streamingMessageByTopic.TryGetValue(topicId, out var streaming) &&
            streaming is not null &&
            !string.IsNullOrEmpty(streaming.Content))
        {
            count++;
        }

        return count;
    }

    public int GetLastReadCount(string topicId)
    {
        if (_lastSeenMessageCountByTopic.TryGetValue(topicId, out var cached))
        {
            return cached;
        }

        return _topics.FirstOrDefault(t => t.TopicId == topicId)?.LastReadMessageCount ?? 0;
    }

    public void MarkTopicAsSeen(string topicId, int assistantMessageCount)
    {
        _lastSeenMessageCountByTopic[topicId] = assistantMessageCount;

        var topic = _topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is not null)
        {
            topic.LastReadMessageCount = assistantMessageCount;
        }
    }

    public void SetApprovalRequest(ToolApprovalRequestMessage? request)
    {
        CurrentApprovalRequest = request;
        NotifyStateChanged();
    }

    public void AddToolCallsToStreamingMessage(string topicId, string toolCalls)
    {
        if (!_streamingMessageByTopic.TryGetValue(topicId, out var streamingMessage))
        {
            streamingMessage = new ChatMessageModel { Role = "assistant" };
            _streamingMessageByTopic[topicId] = streamingMessage;
            _streamingTopics.Add(topicId);
        }

        if (streamingMessage is null)
        {
            return;
        }

        if (streamingMessage.ToolCalls?.Contains(toolCalls) == true)
        {
            return;
        }

        _streamingMessageByTopic[topicId] = streamingMessage with
        {
            ToolCalls = string.IsNullOrEmpty(streamingMessage.ToolCalls)
                ? toolCalls
                : streamingMessage.ToolCalls + "\n" + toolCalls
        };

        NotifyStateChanged();
    }

    public void NotifyStateChanged()
    {
        OnStateChanged?.Invoke();
    }
}