using Domain.DTOs;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task SendResponse(long chatId, ChatResponseMessage responseMessage, long? threadId,
        CancellationToken cancellationToken);

    Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken);
    Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken);
    Task BlockWhile(long chatId, long? threadId, Func<CancellationToken, Task> task, CancellationToken ct);
}