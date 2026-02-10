using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public record MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages) : IAction;

public record AddMessage(string TopicId, ChatMessageModel Message, string? StreamMessageId = null) : IAction;

public record UpdateMessage(string TopicId, string MessageId, ChatMessageModel Message) : IAction;

public record RemoveLastMessage(string TopicId) : IAction;

public record ClearMessages(string TopicId) : IAction;

public record ClearAllMessages : IAction;