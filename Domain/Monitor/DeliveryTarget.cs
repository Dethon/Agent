using Domain.Contracts;

namespace Domain.Monitor;

public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId, bool Minted = false, string? Address = null);