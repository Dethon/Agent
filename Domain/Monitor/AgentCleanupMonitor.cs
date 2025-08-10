using Domain.Agents;
using Domain.Contracts;

namespace Domain.Monitor;

public class AgentCleanupMonitor(
    AgentResolver agentResolver,
    IChatMessengerClient chatMessengerClient)
{
    public async Task Check(CancellationToken ct)
    {
        foreach (var (chatId, threadId) in agentResolver.Agents)
        {
            if (!await chatMessengerClient.DoesThreadExist(chatId, threadId, ct))
            {
                await agentResolver.Clean(chatId, threadId);
            }
        }
    }
}