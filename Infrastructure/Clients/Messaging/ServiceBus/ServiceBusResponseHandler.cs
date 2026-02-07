using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter)
{
    public async Task ProcessAsync(
        IAsyncEnumerable<(AgentKey Key, AgentResponseUpdate Update, AiResponse? Response, MessageSource Source)>
            updates,
        CancellationToken ct)
    {
        var completedResponses = updates
            .Select(x => (x.Key, x.Response?.Content, CorrelationId: GetCorrelationId(x.Key.ChatId)))
            .Where(x => !string.IsNullOrEmpty(x.Content) && x.CorrelationId is not null)
            .Select(x => (x.Key, Content: x.Content!, CorrelationId: x.CorrelationId!))
            .WithCancellation(ct);

        await foreach (var (key, content, correlationId) in completedResponses)
        {
            await responseWriter.WriteResponseAsync(correlationId, key.AgentId!, content, ct);
        }
    }

    private string? GetCorrelationId(long chatId)
    {
        return promptReceiver.TryGetCorrelationId(chatId, out var correlationId)
            ? correlationId
            : null;
    }
}
