using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Infrastructure.Clients.Messaging.ServiceBus;

public class ServiceBusResponseHandler(
    ServiceBusPromptReceiver promptReceiver,
    ServiceBusResponseWriter responseWriter,
    string defaultAgentId)
{
    public async Task ProcessAsync(
        IAsyncEnumerable<(AgentKey Key, AgentResponseUpdate Update, AiResponse? Response, MessageSource Source)>
            updates,
        CancellationToken ct)
    {
        var completedResponses = updates
            .Select(x => (x.Key, x.Response?.Content, SourceId: GetSourceId(x.Key.ChatId)))
            .Where(x => !string.IsNullOrEmpty(x.Content) && x.SourceId is not null)
            .Select(x => (x.Key, Content: x.Content!, SourceId: x.SourceId!))
            .WithCancellation(ct);

        await foreach (var (key, content, sourceId) in completedResponses)
        {
            await responseWriter.WriteResponseAsync(sourceId, key.AgentId ?? defaultAgentId, content, ct);
        }
    }

    private string? GetSourceId(long chatId)
    {
        return promptReceiver.TryGetSourceId(chatId, out var sourceId)
            ? sourceId
            : null;
    }
}