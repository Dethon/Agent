using Domain.Contracts;

namespace Infrastructure.Storage;

public class InMemoryConversationHistoryStore : IConversationHistoryStore
{
    private readonly Dictionary<string, byte[]> _store = [];

    public Task<byte[]?> GetAsync(string conversationId, CancellationToken ct)
    {
        _store.TryGetValue(conversationId, out var data);
        return Task.FromResult(data);
    }

    public Task SaveAsync(string conversationId, byte[] data, CancellationToken ct)
    {
        _store[conversationId] = data;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string conversationId, CancellationToken ct)
    {
        _store.Remove(conversationId);
        return Task.CompletedTask;
    }
}