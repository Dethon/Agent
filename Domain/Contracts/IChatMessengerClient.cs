using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    bool SupportsScheduledNotifications { get; }

    MessageSource Source { get; }

    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken);

    Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken);

    Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default);

    Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default);
}