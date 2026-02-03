using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients) : IChatMessengerClient
{
    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        return clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates,
        CancellationToken cancellationToken)
    {
        var channels = clients.Select(_ => Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?)>()).ToArray();

        var broadcastTask = BroadcastUpdatesAsync(updates, channels, cancellationToken);

        var processTasks = clients
            .Select((client, i) => client.ProcessResponseStreamAsync(
                channels[i].Reader.ReadAllAsync(cancellationToken),
                cancellationToken))
            .ToArray();

        await broadcastTask;
        await Task.WhenAll(processTasks);
    }

    public Task<int> CreateThread(long chatId, string name, string? agentId, CancellationToken cancellationToken)
    {
        return clients[0].CreateThread(chatId, name, agentId, cancellationToken);
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        foreach (var client in clients)
        {
            if (await client.DoesThreadExist(chatId, threadId, agentId, cancellationToken))
            {
                return true;
            }
        }
        return false;
    }

    public Task<AgentKey> CreateTopicIfNeededAsync(
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        return clients[0].CreateTopicIfNeededAsync(chatId, threadId, agentId, topicName, ct);
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, CancellationToken ct = default)
    {
        await Task.WhenAll(clients.Select(c => c.StartScheduledStreamAsync(agentKey, ct)));
    }

    private static async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> source,
        Channel<(AgentKey, AgentResponseUpdate, AiResponse?)>[] channels,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                await Task.WhenAll(channels.Select(c => c.Writer.WriteAsync(update, ct).AsTask()));
            }
        }
        finally
        {
            foreach (var channel in channels)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}
