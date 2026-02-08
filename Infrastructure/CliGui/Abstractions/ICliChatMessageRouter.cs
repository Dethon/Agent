using Domain.DTOs;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Infrastructure.CliGui.Abstractions;

public interface ICliChatMessageRouter : IDisposable
{
    long ChatId { get; }
    int ThreadId { get; }

    event Action? ShutdownRequested;

    IEnumerable<ChatPrompt> ReadPrompts(CancellationToken cancellationToken);
    void SendResponse(ChatResponseMessage responseMessage);

    void RestoreHistory(IReadOnlyList<AiChatMessage> messages);
}