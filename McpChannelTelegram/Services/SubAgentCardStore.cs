using System.Collections.Concurrent;

namespace McpChannelTelegram.Services;

public sealed class SubAgentCardStore : ISubAgentCardStore
{
    private readonly ConcurrentDictionary<string, SubAgentCard> _cards = new();

    public void Track(string handle, long chatId, int messageId, string subAgentId) =>
        _cards[handle] = new SubAgentCard(chatId, messageId, subAgentId);

    public bool TryGet(string handle, out SubAgentCard card) =>
        _cards.TryGetValue(handle, out card!);

    public void Remove(string handle) =>
        _cards.TryRemove(handle, out _);
}
