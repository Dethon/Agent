using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusChatMessengerClient(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseHandler responseHandler,
    string defaultAgentId) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => false;
    public MessageSource Source => MessageSource.ServiceBus;

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(
        int timeout,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        => promptReceiver.ReadPromptsAsync(cancellationToken);

    public Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
        => responseHandler.ProcessAsync(updates, cancellationToken);

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
        => Task.FromResult(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? defaultAgentId));

    public Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
        => Task.CompletedTask;
}
