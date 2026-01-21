using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public record LoadMessages(string TopicId) : IAction;

public record MessagesLoaded(string TopicId, IReadOnlyList<ChatMessageModel> Messages) : IAction;

public record AddMessage(string TopicId, ChatMessageModel Message) : IAction;

public record UpdateMessage(string TopicId, string MessageId, ChatMessageModel Message) : IAction;

public record RemoveLastMessage(string TopicId) : IAction;

public record ClearMessages(string TopicId) : IAction;