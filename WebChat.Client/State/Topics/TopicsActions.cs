using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

/// <summary>
/// Actions for topic state management.
/// </summary>

/// <summary>
/// Initiates loading of topics (sets IsLoading = true).
/// </summary>
public record LoadTopics : IAction;

/// <summary>
/// Topics have been loaded from server.
/// </summary>
public record TopicsLoaded(IReadOnlyList<StoredTopic> Topics) : IAction;

/// <summary>
/// Select a topic by ID (or null to deselect).
/// </summary>
public record SelectTopic(string? TopicId) : IAction;

/// <summary>
/// Add a new topic to the list.
/// </summary>
public record AddTopic(StoredTopic Topic) : IAction;

/// <summary>
/// Update an existing topic.
/// </summary>
public record UpdateTopic(StoredTopic Topic) : IAction;

/// <summary>
/// Remove a topic by ID. ChatId and ThreadId are optional - if provided, deletes from server.
/// When triggered by server notification, these are null (server already deleted).
/// </summary>
public record RemoveTopic(string TopicId, long? ChatId = null, long? ThreadId = null) : IAction;

/// <summary>
/// Set the available agents.
/// </summary>
public record SetAgents(IReadOnlyList<AgentInfo> Agents) : IAction;

/// <summary>
/// Select an agent by ID.
/// </summary>
public record SelectAgent(string AgentId) : IAction;

/// <summary>
/// Set an error state for topics.
/// </summary>
public record TopicsError(string Message) : IAction;

/// <summary>
/// Create a new topic (deselect current topic for new conversation).
/// </summary>
public record CreateNewTopic : IAction;

/// <summary>
/// Triggers app initialization (load agents, topics).
/// </summary>
public record Initialize : IAction;
