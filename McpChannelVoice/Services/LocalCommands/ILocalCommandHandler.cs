namespace McpChannelVoice.Services.LocalCommands;

// A destination for local voice commands: an action the hub performs itself, without the agent.
// Registered as a DI collection; LocalCommandDispatcher routes by the Commands set each handler
// declares. HandleAsync's bool means "the action reached its destination" and drives the
// command/command_failed telemetry outcome upstream.
//
// This contract deliberately lives in the voice channel, not Domain: handlers act on a
// SatelliteSession, and other subsystems are separate processes reachable only via MCP/Redis.
// If a second consumer ever appears, promote it to the shared layers then.
public interface ILocalCommandHandler
{
    IReadOnlySet<VoiceCommand> Commands { get; }
    Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct);
}