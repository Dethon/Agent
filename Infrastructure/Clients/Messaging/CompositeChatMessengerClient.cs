using System.Threading.Channels;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging;

public sealed class CompositeChatMessengerClient(
    IReadOnlyList<IChatMessengerClient> clients,
    IMessageSourceRouter router) : IChatMessengerClient
{
    public MessageSource Source => MessageSource.WebUi;

    public bool SupportsScheduledNotifications => clients.Any(c => c.SupportsScheduledNotifications);

    public IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken)
    {
        Validate();
        return clients
            .Select(c => c.ReadPrompts(timeout, cancellationToken))
            .Merge(cancellationToken);
    }

    public async Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken cancellationToken)
    {
        Validate();
        var channels = clients
            .Select(_ => Channel.CreateUnbounded<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)>())
            .ToArray();

        var clientChannelPairs = clients
            .Zip(channels, (client, channel) => (client, channel))
            .ToArray();

        var broadcastTask = BroadcastUpdatesAsync(updates, clientChannelPairs, cancellationToken);

        var processTasks = clientChannelPairs
            .Select(pair => pair.client.ProcessResponseStreamAsync(
                pair.channel.Reader.ReadAllAsync(cancellationToken),
                cancellationToken))
            .ToArray();

        await broadcastTask;
        await Task.WhenAll(processTasks);
    }

    public async Task<bool> DoesThreadExist(long chatId, long threadId, string? agentId,
        CancellationToken cancellationToken)
    {
        Validate();
        var existsTasks = clients
            .Select(client => client.DoesThreadExist(chatId, threadId, agentId, cancellationToken));
        var results = await Task.WhenAll(existsTasks);
        return results.Any(exists => exists);
    }

    public async Task<AgentKey> CreateTopicIfNeededAsync(
        MessageSource source,
        long? chatId,
        long? threadId,
        string? agentId,
        string? topicName,
        CancellationToken ct = default)
    {
        Validate();
        var tasks = router.GetClientsForSource(clients, source)
            .Select(c => c.CreateTopicIfNeededAsync(source, chatId, threadId, agentId, topicName, ct));
        var results = await Task.WhenAll(tasks);

        return results
            .DefaultIfEmpty(new AgentKey(chatId ?? 0, threadId ?? 0, agentId ?? string.Empty))
            .First();
    }

    public async Task StartScheduledStreamAsync(AgentKey agentKey, MessageSource source, CancellationToken ct = default)
    {
        Validate();
        var tasks = router.GetClientsForSource(clients, source)
            .Select(c => c.StartScheduledStreamAsync(agentKey, source, ct));
        await Task.WhenAll(tasks);
    }

    private void Validate()
    {
        if (clients.Count == 0)
        {
            throw new InvalidOperationException($"{nameof(clients)} must contain at least one client");
        }
    }

    private async Task BroadcastUpdatesAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> source,
        (IChatMessengerClient client, Channel<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> channel)[]
            clientChannelPairs,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                var (_, _, _, messageSource) = update;
                var targetClients = router.GetClientsForSource(clients, messageSource).ToHashSet();

                var writeTasks = clientChannelPairs
                    .Where(pair => targetClients.Contains(pair.client))
                    .Select(pair => pair.channel.Writer.WriteAsync(update, ct).AsTask());

                await Task.WhenAll(writeTasks);
            }
        }
        finally
        {
            foreach (var (_, channel) in clientChannelPairs)
            {
                channel.Writer.TryComplete();
            }
        }
    }
}