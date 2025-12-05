using Domain.Agents;
using Domain.Contracts;

namespace Domain.Monitor;

public class AgentCleanupMonitor(
    ThreadResolver threadResolver,
    CancellationResolver cancellationResolver,
    IChatMessengerClient chatMessengerClient)
{
    public async Task Check(CancellationToken ct)
    {
        foreach (var (chatId, threadId) in threadResolver.Threads)
        {
            if (!await chatMessengerClient.DoesThreadExist(chatId, threadId, ct))
            {
                threadResolver.Clean(chatId, threadId);
                cancellationResolver.Clean(chatId, threadId);
            }
        }
    }
}