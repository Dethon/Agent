using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

/// <summary>
/// Fine-grained actions for message state management.
/// </summary>

/// <summary>
/// Initiates loading of messages for a topic.
/// </summary>
public record LoadMessages(string TopicId) : IAction;

/// <summary>
/// Messages have been loaded for a topic.
/// </summary>
public record MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages) : IAction;

/// <summary>
/// Add a single message to a topic.
/// </summary>
public record AddMessage(string TopicId, ChatMessageModel Message) : IAction;

/// <summary>
/// Update a specific message within a topic by MessageId.
/// </summary>
public record UpdateMessage(string TopicId, string MessageId, ChatMessageModel Message) : IAction;

/// <summary>
/// Remove the last message from a topic (used for streaming message replacement).
/// </summary>
public record RemoveLastMessage(string TopicId) : IAction;

/// <summary>
/// Clear all messages for a topic.
/// </summary>
public record ClearMessages(string TopicId) : IAction;
