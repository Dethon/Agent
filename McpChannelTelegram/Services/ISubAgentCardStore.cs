namespace McpChannelTelegram.Services;

public interface ISubAgentCardStore
{
    void Track(string handle, long chatId, int messageId, string subAgentId);
    bool TryGet(string handle, out SubAgentCard card);
    void Remove(string handle);
}

public sealed record SubAgentCard(long ChatId, int MessageId, string SubAgentId);
