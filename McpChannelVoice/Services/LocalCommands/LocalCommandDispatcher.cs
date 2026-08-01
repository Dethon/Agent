namespace McpChannelVoice.Services.LocalCommands;

public sealed record LocalCommandResult(VoiceCommand Command, bool Sent);

public sealed class LocalCommandDispatcher
{
    private readonly VoiceCommandMatcher _matcher;
    private readonly IReadOnlyDictionary<VoiceCommand, ILocalCommandHandler> _routes;

    public LocalCommandDispatcher(VoiceCommandMatcher matcher, IEnumerable<ILocalCommandHandler> handlers)
    {
        _matcher = matcher;

        // Both checks throw at container build time, so a routing mistake is a startup crash
        // rather than a command silently dropped (or double-handled) on the first utterance.
        var claims = handlers
            .SelectMany(h => h.Commands.Select(command => (Command: command, Handler: h)))
            .ToList();

        var duplicates = claims
            .GroupBy(c => c.Command)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Local commands owned by more than one handler: {string.Join(", ", duplicates)}");
        }

        _routes = claims.ToDictionary(c => c.Command, c => c.Handler);

        var uncovered = Enum.GetValues<VoiceCommand>().Where(c => !_routes.ContainsKey(c)).ToList();
        if (uncovered.Count > 0)
        {
            throw new InvalidOperationException(
                $"Local commands with no registered handler: {string.Join(", ", uncovered)}");
        }
    }

    public async Task<LocalCommandResult?> TryHandleAsync(
        string transcript, SatelliteSession session, CancellationToken ct) =>
        _matcher.Match(transcript) is { } command
            ? new LocalCommandResult(command, await _routes[command].HandleAsync(command, session, ct))
            : null;
}