using Domain.Agents;
using Domain.Contracts;

namespace Domain.Monitor;

public class AgentCleanupMonitor(
    ChatThreadResolver threadResolver,
    IChatMessengerClient chatMessengerClient)
{
    public async Task Check(CancellationToken ct)
    {
        foreach (var agentKey in threadResolver.AgentKeys)
        {
            var chatId = agentKey.ChatId;
            var threadId = agentKey.ThreadId;
            if (!await chatMessengerClient.DoesThreadExist(chatId, threadId, ct))
            {
                await threadResolver.ClearAsync(agentKey);
            }
        }
    }
}