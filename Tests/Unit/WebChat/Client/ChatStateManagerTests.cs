using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.Services.State;

namespace Tests.Unit.WebChat.Client;

public sealed class ChatStateManagerTests
{
    private readonly ChatStateManager _stateManager = new();

    private static StoredTopic CreateTopic(string? topicId = null, string? agentId = null)
    {
        return new StoredTopic
        {
            TopicId = topicId ?? Guid.NewGuid().ToString(),
            ChatId = Random.Shared.NextInt64(1000, 9999),
            ThreadId = Random.Shared.NextInt64(1000, 9999),
            AgentId = agentId ?? "test-agent",
            Name = "Test Topic",
            CreatedAt = DateTime.UtcNow
        };
    }

    #region Agent Operations

    [Fact]
    public void SetAgents_WithMultipleAgents_UpdatesAgentsList()
    {
        var agents = new List<AgentInfo>
        {
            new("agent-1", "Agent 1", "First agent"),
            new("agent-2", "Agent 2", "Second agent")
        };

        _stateManager.SetAgents(agents);

        _stateManager.Agents.Count.ShouldBe(2);
        _stateManager.Agents.ShouldContain(a => a.Id == "agent-1");
        _stateManager.Agents.ShouldContain(a => a.Id == "agent-2");
    }

    [Fact]
    public void SetAgents_ReplacesExistingAgents()
    {
        var initialAgents = new List<AgentInfo> { new("old-agent", "Old", null) };
        var newAgents = new List<AgentInfo> { new("new-agent", "New", null) };

        _stateManager.SetAgents(initialAgents);
        _stateManager.SetAgents(newAgents);

        _stateManager.Agents.Count.ShouldBe(1);
        _stateManager.Agents[0].Id.ShouldBe("new-agent");
    }

    [Fact]
    public void SelectAgent_ChangesSelectedAgent()
    {
        _stateManager.SelectAgent("agent-1");

        _stateManager.SelectedAgentId.ShouldBe("agent-1");
    }

    [Fact]
    public void SelectAgent_ClearsSelectedTopic()
    {
        var topic = CreateTopic(agentId: "agent-1");
        _stateManager.AddTopic(topic);
        _stateManager.SelectTopic(topic);

        _stateManager.SelectAgent("agent-2");

        _stateManager.SelectedTopic.ShouldBeNull();
    }

    [Fact]
    public void SelectAgent_SameAgent_DoesNotTriggerStateChanged()
    {
        _stateManager.SelectAgent("agent-1");
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.SelectAgent("agent-1");

        triggered.ShouldBeFalse();
    }

    #endregion

    #region Topic Operations

    [Fact]
    public void SelectTopic_UpdatesSelection()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);

        _stateManager.SelectTopic(topic);

        _stateManager.SelectedTopic.ShouldBe(topic);
    }

    [Fact]
    public void SelectTopic_SetsAgentId()
    {
        var topic = CreateTopic(agentId: "topic-agent");
        _stateManager.AddTopic(topic);

        _stateManager.SelectTopic(topic);

        _stateManager.SelectedAgentId.ShouldBe("topic-agent");
    }

    [Fact]
    public void SelectTopic_WithNull_ClearsSelection()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SelectTopic(topic);

        _stateManager.SelectTopic(null);

        _stateManager.SelectedTopic.ShouldBeNull();
    }

    [Fact]
    public void AddTopic_WithNewTopic_AddsToList()
    {
        var topic = CreateTopic();

        _stateManager.AddTopic(topic);

        _stateManager.Topics.Count.ShouldBe(1);
        _stateManager.Topics[0].ShouldBe(topic);
    }

    [Fact]
    public void AddTopic_WithDuplicateTopicId_DoesNotDuplicate()
    {
        var topic = CreateTopic(topicId: "same-id");
        var duplicate = CreateTopic(topicId: "same-id");

        _stateManager.AddTopic(topic);
        _stateManager.AddTopic(duplicate);

        _stateManager.Topics.Count.ShouldBe(1);
    }

    [Fact]
    public void RemoveTopic_WithExistingTopic_RemovesFromList()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);

        _stateManager.RemoveTopic(topic.TopicId);

        _stateManager.Topics.ShouldBeEmpty();
    }

    [Fact]
    public void RemoveTopic_CleansUpRelatedState()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, [new ChatMessageModel { Content = "Test" }]);
        _stateManager.StartStreaming(topic.TopicId);
        _stateManager.MarkTopicAsSeen(topic.TopicId, 1);

        _stateManager.RemoveTopic(topic.TopicId);

        _stateManager.HasMessagesForTopic(topic.TopicId).ShouldBeFalse();
        _stateManager.IsTopicStreaming(topic.TopicId).ShouldBeFalse();
        _stateManager.GetStreamingMessageForTopic(topic.TopicId).ShouldBeNull();
    }

    [Fact]
    public void RemoveTopic_WhenSelected_ClearsSelection()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SelectTopic(topic);

        _stateManager.RemoveTopic(topic.TopicId);

        _stateManager.SelectedTopic.ShouldBeNull();
    }

    [Fact]
    public void RemoveTopic_WhenSelected_ClearsApprovalRequest()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SelectTopic(topic);
        _stateManager.SetApprovalRequest(new ToolApprovalRequestMessage("approval-1", []));

        _stateManager.RemoveTopic(topic.TopicId);

        _stateManager.CurrentApprovalRequest.ShouldBeNull();
    }

    [Fact]
    public void UpdateTopic_WithMetadata_UpdatesProperties()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _stateManager.AddTopic(topic);
        var metadata = new TopicMetadata(
            "topic-1", 100, 200, "agent", "Updated Name",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 5);

        _stateManager.UpdateTopic(metadata);

        var updated = _stateManager.GetTopicById("topic-1");
        updated.ShouldNotBeNull();
        updated.Name.ShouldBe("Updated Name");
        updated.LastReadMessageCount.ShouldBe(5);
    }

    [Fact]
    public void UpdateTopic_WithNonExistentTopic_DoesNothing()
    {
        var metadata = new TopicMetadata(
            "non-existent", 100, 200, "agent", "Name",
            DateTimeOffset.UtcNow, null);

        Should.NotThrow(() => _stateManager.UpdateTopic(metadata));
    }

    [Fact]
    public void GetTopicById_WithValidId_ReturnsTopic()
    {
        var topic = CreateTopic(topicId: "find-me");
        _stateManager.AddTopic(topic);

        var found = _stateManager.GetTopicById("find-me");

        found.ShouldBe(topic);
    }

    [Fact]
    public void GetTopicById_WithInvalidId_ReturnsNull()
    {
        var found = _stateManager.GetTopicById("does-not-exist");

        found.ShouldBeNull();
    }

    #endregion

    #region Message Operations

    [Fact]
    public void GetMessagesForTopic_WithNewTopic_CreatesEmptyList()
    {
        var messages = _stateManager.GetMessagesForTopic("new-topic");

        messages.ShouldNotBeNull();
        messages.ShouldBeEmpty();
    }

    [Fact]
    public void SetMessagesForTopic_UpdatesMessages()
    {
        var topicId = "topic-1";
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi" }
        };

        _stateManager.SetMessagesForTopic(topicId, messages);

        var retrieved = _stateManager.GetMessagesForTopic(topicId);
        retrieved.Count.ShouldBe(2);
    }

    [Fact]
    public void SetMessagesForTopic_TriggersStateChanged()
    {
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.SetMessagesForTopic("topic-1", []);

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void HasMessagesForTopic_WithMessages_ReturnsTrue()
    {
        _stateManager.SetMessagesForTopic("topic-1", [new ChatMessageModel { Content = "Test" }]);

        _stateManager.HasMessagesForTopic("topic-1").ShouldBeTrue();
    }

    [Fact]
    public void HasMessagesForTopic_WithoutMessages_ReturnsFalse()
    {
        _stateManager.HasMessagesForTopic("non-existent").ShouldBeFalse();
    }

    [Fact]
    public void AddMessage_AppendsToList()
    {
        var topicId = "topic-1";
        _stateManager.SetMessagesForTopic(topicId, [new ChatMessageModel { Content = "First" }]);

        _stateManager.AddMessage(topicId, new ChatMessageModel { Content = "Second" });

        var messages = _stateManager.GetMessagesForTopic(topicId);
        messages.Count.ShouldBe(2);
        messages[1].Content.ShouldBe("Second");
    }

    [Fact]
    public void AddMessage_TriggersStateChanged()
    {
        _stateManager.SetMessagesForTopic("topic-1", []);
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.AddMessage("topic-1", new ChatMessageModel { Content = "Test" });

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void UpdateStreamingMessage_DoesNotTriggerStateChanged()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.StartStreaming(topic.TopicId);
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.UpdateStreamingMessage(topic.TopicId, new ChatMessageModel { Content = "Streaming" });

        triggered.ShouldBeFalse();
    }

    [Fact]
    public void GetStreamingMessageForTopic_WithoutStreaming_ReturnsNull()
    {
        _stateManager.GetStreamingMessageForTopic("non-existent").ShouldBeNull();
    }

    [Fact]
    public void CurrentMessages_WhenTopicSelected_ReturnsMessages()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic(topic.TopicId, [new ChatMessageModel { Content = "Test" }]);
        _stateManager.SelectTopic(topic);

        _stateManager.CurrentMessages.Count.ShouldBe(1);
        _stateManager.CurrentMessages[0].Content.ShouldBe("Test");
    }

    [Fact]
    public void CurrentMessages_WhenNoTopicSelected_ReturnsEmpty()
    {
        _stateManager.SetMessagesForTopic("some-topic", [new ChatMessageModel { Content = "Test" }]);

        _stateManager.CurrentMessages.ShouldBeEmpty();
    }

    [Fact]
    public void CurrentStreamingMessage_WhenTopicSelectedAndStreaming_ReturnsMessage()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SelectTopic(topic);
        _stateManager.StartStreaming(topic.TopicId);
        _stateManager.UpdateStreamingMessage(topic.TopicId, new ChatMessageModel { Content = "Streaming" });

        _stateManager.CurrentStreamingMessage.ShouldNotBeNull();
        _stateManager.CurrentStreamingMessage.Content.ShouldBe("Streaming");
    }

    #endregion

    #region Streaming State

    [Fact]
    public void StartStreaming_AddsToStreamingTopics()
    {
        var topicId = "topic-1";

        _stateManager.StartStreaming(topicId);

        _stateManager.IsTopicStreaming(topicId).ShouldBeTrue();
        _stateManager.StreamingTopics.ShouldContain(topicId);
    }

    [Fact]
    public void StartStreaming_CreatesEmptyStreamingMessage()
    {
        var topicId = "topic-1";

        _stateManager.StartStreaming(topicId);

        var msg = _stateManager.GetStreamingMessageForTopic(topicId);
        msg.ShouldNotBeNull();
        msg.Role.ShouldBe("assistant");
        msg.Content.ShouldBeEmpty();
    }

    [Fact]
    public void StopStreaming_RemovesFromStreamingTopics()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);

        _stateManager.StopStreaming(topicId);

        _stateManager.IsTopicStreaming(topicId).ShouldBeFalse();
    }

    [Fact]
    public void StopStreaming_ClearsStreamingMessage()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);
        _stateManager.UpdateStreamingMessage(topicId, new ChatMessageModel { Content = "Test" });

        _stateManager.StopStreaming(topicId);

        _stateManager.GetStreamingMessageForTopic(topicId).ShouldBeNull();
    }

    [Fact]
    public void IsTopicStreaming_WithActiveStream_ReturnsTrue()
    {
        _stateManager.StartStreaming("topic-1");

        _stateManager.IsTopicStreaming("topic-1").ShouldBeTrue();
    }

    [Fact]
    public void IsTopicStreaming_WithoutStream_ReturnsFalse()
    {
        _stateManager.IsTopicStreaming("topic-1").ShouldBeFalse();
    }

    [Fact]
    public void IsCurrentTopicStreaming_WhenSelectedAndStreaming_ReturnsTrue()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        _stateManager.SelectTopic(topic);
        _stateManager.StartStreaming(topic.TopicId);

        _stateManager.IsCurrentTopicStreaming.ShouldBeTrue();
    }

    [Fact]
    public void IsCurrentTopicStreaming_WhenNotSelected_ReturnsFalse()
    {
        _stateManager.StartStreaming("some-topic");

        _stateManager.IsCurrentTopicStreaming.ShouldBeFalse();
    }

    [Fact]
    public void TryStartResuming_FirstCall_ReturnsTrue()
    {
        _stateManager.TryStartResuming("topic-1").ShouldBeTrue();
    }

    [Fact]
    public void TryStartResuming_SecondCall_ReturnsFalse()
    {
        _stateManager.TryStartResuming("topic-1");

        _stateManager.TryStartResuming("topic-1").ShouldBeFalse();
    }

    [Fact]
    public void IsTopicResuming_WhenResuming_ReturnsTrue()
    {
        _stateManager.TryStartResuming("topic-1");

        _stateManager.IsTopicResuming("topic-1").ShouldBeTrue();
    }

    [Fact]
    public void StopResuming_AllowsNewResume()
    {
        _stateManager.TryStartResuming("topic-1");
        _stateManager.StopResuming("topic-1");

        _stateManager.TryStartResuming("topic-1").ShouldBeTrue();
    }

    #endregion

    #region Unread Tracking

    [Fact]
    public void GetAssistantMessageCount_CountsNonUserMessages()
    {
        var topicId = "topic-1";
        _stateManager.SetMessagesForTopic(topicId, [
            new ChatMessageModel { Role = "user", Content = "Hello" },
            new ChatMessageModel { Role = "assistant", Content = "Hi" },
            new ChatMessageModel { Role = "assistant", Content = "How can I help?" }
        ]);

        _stateManager.GetAssistantMessageCount(topicId).ShouldBe(2);
    }

    [Fact]
    public void GetAssistantMessageCount_IncludesStreamingWithContent()
    {
        var topicId = "topic-1";
        _stateManager.SetMessagesForTopic(topicId, [
            new ChatMessageModel { Role = "assistant", Content = "First" }
        ]);
        _stateManager.StartStreaming(topicId);
        _stateManager.UpdateStreamingMessage(topicId, new ChatMessageModel { Content = "Streaming" });

        _stateManager.GetAssistantMessageCount(topicId).ShouldBe(2);
    }

    [Fact]
    public void GetAssistantMessageCount_ExcludesStreamingWithoutContent()
    {
        var topicId = "topic-1";
        _stateManager.SetMessagesForTopic(topicId, [
            new ChatMessageModel { Role = "assistant", Content = "First" }
        ]);
        _stateManager.StartStreaming(topicId);

        _stateManager.GetAssistantMessageCount(topicId).ShouldBe(1);
    }

    [Fact]
    public void GetLastReadCount_ReturnsFromCache()
    {
        var topicId = "topic-1";
        _stateManager.MarkTopicAsSeen(topicId, 5);

        _stateManager.GetLastReadCount(topicId).ShouldBe(5);
    }

    [Fact]
    public void GetLastReadCount_FallsBackToTopic()
    {
        var topic = CreateTopic(topicId: "topic-1");
        topic.LastReadMessageCount = 3;
        _stateManager.AddTopic(topic);

        _stateManager.GetLastReadCount("topic-1").ShouldBe(3);
    }

    [Fact]
    public void MarkTopicAsSeen_UpdatesCacheAndTopic()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _stateManager.AddTopic(topic);

        _stateManager.MarkTopicAsSeen("topic-1", 7);

        _stateManager.GetLastReadCount("topic-1").ShouldBe(7);
        topic.LastReadMessageCount.ShouldBe(7);
    }

    [Fact]
    public void UnreadCounts_ExcludesSelectedTopic()
    {
        var selectedTopic = CreateTopic(topicId: "selected");
        var otherTopic = CreateTopic(topicId: "other");
        _stateManager.AddTopic(selectedTopic);
        _stateManager.AddTopic(otherTopic);
        _stateManager.SelectTopic(selectedTopic);
        _stateManager.SetMessagesForTopic("selected", [
            new ChatMessageModel { Role = "assistant", Content = "msg" }
        ]);
        _stateManager.SetMessagesForTopic("other", [
            new ChatMessageModel { Role = "assistant", Content = "msg" }
        ]);

        var unread = _stateManager.UnreadCounts;

        unread.ContainsKey("selected").ShouldBeFalse();
    }

    [Fact]
    public void UnreadCounts_CalculatesDifference()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "1" },
            new ChatMessageModel { Role = "assistant", Content = "2" },
            new ChatMessageModel { Role = "assistant", Content = "3" }
        ]);
        _stateManager.MarkTopicAsSeen("topic-1", 1);

        var unread = _stateManager.UnreadCounts;

        unread["topic-1"].ShouldBe(2);
    }

    [Fact]
    public void UnreadCounts_ExcludesTopicsWithNoNewMessages()
    {
        var topic = CreateTopic(topicId: "topic-1");
        _stateManager.AddTopic(topic);
        _stateManager.SetMessagesForTopic("topic-1", [
            new ChatMessageModel { Role = "assistant", Content = "1" }
        ]);
        _stateManager.MarkTopicAsSeen("topic-1", 1);

        var unread = _stateManager.UnreadCounts;

        unread.ContainsKey("topic-1").ShouldBeFalse();
    }

    #endregion

    #region Approval State

    [Fact]
    public void SetApprovalRequest_UpdatesRequest()
    {
        var request = new ToolApprovalRequestMessage("approval-1", []);

        _stateManager.SetApprovalRequest(request);

        _stateManager.CurrentApprovalRequest.ShouldBe(request);
    }

    [Fact]
    public void SetApprovalRequest_TriggersStateChanged()
    {
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.SetApprovalRequest(new ToolApprovalRequestMessage("approval-1", []));

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void SetApprovalRequest_ToNull_ClearsRequest()
    {
        _stateManager.SetApprovalRequest(new ToolApprovalRequestMessage("approval-1", []));

        _stateManager.SetApprovalRequest(null);

        _stateManager.CurrentApprovalRequest.ShouldBeNull();
    }

    [Fact]
    public void AddToolCallsToStreamingMessage_CreatesMessageIfNeeded()
    {
        _stateManager.AddToolCallsToStreamingMessage("topic-1", "tool_call_1");

        var msg = _stateManager.GetStreamingMessageForTopic("topic-1");
        msg.ShouldNotBeNull();
        msg.ToolCalls.ShouldBe("tool_call_1");
    }

    [Fact]
    public void AddToolCallsToStreamingMessage_AppendsToExisting()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);
        _stateManager.AddToolCallsToStreamingMessage(topicId, "tool_1");

        _stateManager.AddToolCallsToStreamingMessage(topicId, "tool_2");

        var msg = _stateManager.GetStreamingMessageForTopic(topicId);
        msg!.ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public void AddToolCallsToStreamingMessage_DeduplicatesToolCalls()
    {
        var topicId = "topic-1";
        _stateManager.StartStreaming(topicId);
        _stateManager.AddToolCallsToStreamingMessage(topicId, "tool_1");

        _stateManager.AddToolCallsToStreamingMessage(topicId, "tool_1");

        var msg = _stateManager.GetStreamingMessageForTopic(topicId);
        msg!.ToolCalls.ShouldBe("tool_1");
    }

    [Fact]
    public void AddToolCallsToStreamingMessage_StartsStreamingIfNotAlready()
    {
        _stateManager.AddToolCallsToStreamingMessage("topic-1", "tool_1");

        _stateManager.IsTopicStreaming("topic-1").ShouldBeTrue();
    }

    #endregion

    #region Event Handling

    [Fact]
    public void NotifyStateChanged_TriggersEvent()
    {
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.NotifyStateChanged();

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void OnStateChanged_TriggeredByAddTopic()
    {
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.AddTopic(CreateTopic());

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void OnStateChanged_TriggeredByRemoveTopic()
    {
        var topic = CreateTopic();
        _stateManager.AddTopic(topic);
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.RemoveTopic(topic.TopicId);

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void OnStateChanged_TriggeredByStartStreaming()
    {
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.StartStreaming("topic-1");

        triggered.ShouldBeTrue();
    }

    [Fact]
    public void OnStateChanged_TriggeredByStopStreaming()
    {
        _stateManager.StartStreaming("topic-1");
        var triggered = false;
        _stateManager.OnStateChanged += () => triggered = true;

        _stateManager.StopStreaming("topic-1");

        triggered.ShouldBeTrue();
    }

    #endregion
}