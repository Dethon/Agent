using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => false;
    public MessageSource Source => MessageSource.ServiceBus;

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        return promptReceiver.ReadPromptsAsync(cancellationToken);
    }

    public Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
    {
        return responseHandler.ProcessAsync(updates, cancellationToken);
    }

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken)
    {
        return Task.FromResult(false);
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        string? sender = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId));
    }

    public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}