using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Messaging;

public sealed class ServiceBusResponseHandler(
    ServiceBusPromptReceiver receiver,
    ServiceBusResponseWriter writer,
    string defaultAgentId,
    ILogger<ServiceBusResponseHandler> logger)
{
    public Task ProcessAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?, MessageSource)> updates,
        CancellationToken ct)
    {
        // Stub implementation - will be implemented in GREEN phase
        throw new NotImplementedException();
    }
}
