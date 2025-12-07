using Domain.Agents;
using Domain.Contracts;

namespace Domain.Monitor;

public class AgentCleanupMonitor(ChannelResolver channelResolver, IChatMessengerClient chatMessengerClient)
{
    public async Task Check(CancellationToken ct)
    {
        foreach (var agentKey in channelResolver.Channels)
        {
            var chatId = agentKey.ChatId;
            var threadId = agentKey.ThreadId;
            if (!await chatMessengerClient.DoesThreadExist(chatId, threadId, ct))
            {
                channelResolver.Clean(agentKey);
            }
        }
    }
}