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
            if (!await chatMessengerClient.DoesThreadExist(
                    agentKey.ChatId, agentKey.ThreadId, agentKey.BotTokenHash, ct))
            {
                await threadResolver.ClearAsync(agentKey);
            }
        }
    }
}