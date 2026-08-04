using Domain.Contracts;

namespace Domain.Monitor;

// Minted means "minted while resolving this turn", so it is only ever true on the turn that
// created the conversation. A later turn reusing these targets carries it cleared.
public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId, bool Minted = false, string? Address = null);