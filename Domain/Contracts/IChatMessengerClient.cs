using Domain.DTOs;

namespace Domain.Contracts;

public interface IChatMessengerClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken = default);

    Task<int> SendResponse(long chatId, string response, int? messageThreadId = null,
        CancellationToken cancellationToken = default);

    Task<int> CreateThread(long chatId, string name, CancellationToken cancellationToken = default);
}