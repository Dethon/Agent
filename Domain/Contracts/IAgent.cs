using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IAgent
{
    IAsyncEnumerable<ChatMessage> Run(
        string? prompt, bool cancelCurrentOperation, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChatMessage> Run(
        ChatMessage[] prompts, bool cancelCurrentOperation, CancellationToken cancellationToken = default);
}