using Domain.DTOs;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken);

    Task<int> SendResponse(long chatId, string response, long? messageThreadId, CancellationToken cancellationToken);

    Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken);
    Task<bool> DoesThreadExist(long chatId, long threadId, CancellationToken cancellationToken);
}