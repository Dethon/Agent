using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

public record LoadTopics : IAction;

public record TopicsLoaded(IReadOnlyList<StoredTopic> Topics) : IAction;

public record SelectTopic(string? TopicId) : IAction;

public record AddTopic(StoredTopic Topic) : IAction;

public record UpdateTopic(StoredTopic Topic) : IAction;

public record RemoveTopic(string TopicId, string? AgentId = null, long? ChatId = null, long? ThreadId = null) : IAction;

public record SetAgents(IReadOnlyList<AgentInfo> Agents) : IAction;

public record SelectAgent(string AgentId) : IAction;

public record TopicsError(string Message) : IAction;

public record CreateNewTopic : IAction;

public record Initialize : IAction;