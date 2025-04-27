using Domain.DTOs;

namespace Domain.Contracts;

public interface IChatClient
{
    IAsyncEnumerable<ChatPrompt> ReadPrompts(int timeout, CancellationToken cancellationToken = default);
    Task SendResponse(long chatId, string response, int? replyId = null, CancellationToken cancellationToken = default);
}