using Domain.DTOs;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Domain.Contracts;

public interface ILargeLanguageModel
{
    IAsyncEnumerable<ChatResponseUpdate> Prompt(
        IEnumerable<ChatMessage> messages,
        IEnumerable<McpClientTool> tools,
        float? temperature = null,
        CancellationToken cancellationToken = default);
}