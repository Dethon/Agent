using Domain.Conversations;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IConversationFactory
{
    Task<ConversationCreation> CreateAsync(CreateConversationParams p, CancellationToken ct = default);
}