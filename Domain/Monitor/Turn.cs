using Domain.DTOs;
using Domain.Metrics;
using Microsoft.Agents.AI;

namespace Domain.Monitor;

// One message the agent answers, together with where its replies go and the latency span
// that ends on its first delivered chunk. Minted once, when the turn starts, so an update
// cannot travel with targets belonging to a different message.
internal sealed record Turn(
    ChannelMessage Message,
    IReadOnlyList<DeliveryTarget> Targets,
    LatencyScope FirstReply);

internal sealed record TurnUpdate(AgentResponseUpdate Update, Turn Turn);