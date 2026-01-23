using Domain.Agents;
using Domain.DTOs;
using Microsoft.Agents.AI;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task ProcessResponseStreamAsync(
        IAsyncEnumerable<(AgentKey, AgentResponseUpdate, AiResponse?)> updates, CancellationToken cancellationToken);

    Task<int> CreateThread(long chatId, string name, string? botTokenHash, CancellationToken cancellationToken);
    Task<bool> DoesThreadExist(long chatId, long threadId, string? botTokenHash, CancellationToken cancellationToken);
}