using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Contracts;

public interface IChatStateManager
{
    // State properties
    IReadOnlyList<StoredTopic> Topics { get; }
    StoredTopic? SelectedTopic { get; }
    string? SelectedAgentId { get; }
    IReadOnlyList<AgentInfo> Agents { get; }
    IReadOnlyList<ChatMessageModel> CurrentMessages { get; }
    ChatMessageModel? CurrentStreamingMessage { get; }
    bool IsCurrentTopicStreaming { get; }
    ToolApprovalRequestMessage? CurrentApprovalRequest { get; }
    IReadOnlyDictionary<string, int> UnreadCounts { get; }
    IReadOnlySet<string> StreamingTopics { get; }

    // State change events
    event Action? OnStateChanged;

    // Agent operations
    void SetAgents(IReadOnlyList<AgentInfo> agents);
    void SelectAgent(string agentId);

    // Topic operations
    void SelectTopic(StoredTopic? topic);
    void AddTopic(StoredTopic topic);
    void RemoveTopic(string topicId);
    void UpdateTopic(TopicMetadata metadata);
    StoredTopic? GetTopicById(string topicId);

    // Message operations
    List<ChatMessageModel> GetMessagesForTopic(string topicId);
    void SetMessagesForTopic(string topicId, List<ChatMessageModel> messages);
    bool HasMessagesForTopic(string topicId);
    void AddMessage(string topicId, ChatMessageModel message);
    void UpdateStreamingMessage(string topicId, ChatMessageModel? message);
    ChatMessageModel? GetStreamingMessageForTopic(string topicId);

    // Streaming state
    void StartStreaming(string topicId);
    void StopStreaming(string topicId);
    bool IsTopicStreaming(string topicId);
    bool IsTopicResuming(string topicId);
    bool TryStartResuming(string topicId);
    void StopResuming(string topicId);

    // Unread tracking
    int GetAssistantMessageCount(string topicId);
    int GetLastReadCount(string topicId);
    void MarkTopicAsSeen(string topicId, int assistantMessageCount);

    // Approval state
    void SetApprovalRequest(ToolApprovalRequestMessage? request);

    // Tool calls
    void AddToolCallsToStreamingMessage(string topicId, string toolCalls);

    // Trigger re-render
    void NotifyStateChanged();
}