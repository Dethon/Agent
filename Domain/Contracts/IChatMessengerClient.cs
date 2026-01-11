using Domain.DTOs;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task SendResponse(
        long chatId,
        ChatResponseMessage responseMessage,
        long? threadId,
        string? botTokenHash,
        CancellationToken cancellationToken);

    Task<int> CreateThread(long chatId, string name, string? botTokenHash, CancellationToken cancellationToken);
    Task<bool> DoesThreadExist(long chatId, long threadId, string? botTokenHash, CancellationToken cancellationToken);
}