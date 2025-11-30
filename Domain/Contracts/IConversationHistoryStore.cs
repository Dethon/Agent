namespace Domain.Contracts;

public interface IConversationHistoryStore
{
    Task<byte[]?> GetAsync(string conversationId, CancellationToken ct);
    Task SaveAsync(string conversationId, byte[] data, CancellationToken ct);
    Task DeleteAsync(string conversationId, CancellationToken ct);
}