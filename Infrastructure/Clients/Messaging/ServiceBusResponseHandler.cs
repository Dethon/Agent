using System.Collections.Concurrent;
using System.Text;
using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter,
    string defaultAgentId,
    ILogger<ServiceBusResponseHandler> logger)
{
    private readonly ConcurrentDictionary<long, StringBuilder> _accumulators = new();

    public async Task ProcessAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken ct)
    {
        await foreach (var (key, update, _, _) in updates.WithCancellation(ct))
        {
            if (!promptReceiver.TryGetSourceId(key.ChatId, out var sourceId))
                continue;

            await ProcessUpdateAsync(key, update, sourceId, ct);
        }
    }

    private async Task ProcessUpdateAsync(AgentKey key, AgentResponseUpdate update, string sourceId, CancellationToken ct)
    {
        var accumulator = _accumulators.GetOrAdd(key.ChatId, _ => new StringBuilder());

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                    accumulator.Append(tc.Text);
                    break;

                case StreamCompleteContent when accumulator.Length > 0:
                    await responseWriter.WriteResponseAsync(
                        sourceId, key.AgentId ?? defaultAgentId, accumulator.ToString(), ct);
                    accumulator.Clear();
                    _accumulators.TryRemove(key.ChatId, out _);
                    break;
            }
        }
    }
}
