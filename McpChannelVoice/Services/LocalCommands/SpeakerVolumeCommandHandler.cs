using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services.LocalCommands;

public sealed class SpeakerVolumeCommandHandler : ILocalCommandHandler
{
    private static readonly IReadOnlyDictionary<VoiceCommand, string> _actions =
        new Dictionary<VoiceCommand, string>
        {
            [VoiceCommand.LocalVolumeUp] = "up",
            [VoiceCommand.LocalVolumeDown] = "down",
            [VoiceCommand.LocalMute] = "mute",
            [VoiceCommand.LocalUnmute] = "unmute"
        };

    public IReadOnlySet<VoiceCommand> Commands { get; } = _actions.Keys.ToHashSet();

    public Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct) =>
        session.TrySendControlAsync(
            WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = _actions[command] }), ct);
}