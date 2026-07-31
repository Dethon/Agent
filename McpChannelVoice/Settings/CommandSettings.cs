namespace McpChannelVoice.Settings;

// Phrases the voice hub answers itself, without involving the agent or an LLM. Every phrase
// carries an explicit "local" marker so an ordinary music-volume request ("sube el volumen"),
// which belongs to the agent and Music Assistant, can never match one by accident.
public record CommandSettings
{
    public bool Enabled { get; init; } = true;
    public CommandPhrases Phrases { get; init; } = new();
}

public record CommandPhrases
{
    public IReadOnlyList<string> LocalVolumeUp { get; init; } = [];
    public IReadOnlyList<string> LocalVolumeDown { get; init; } = [];
    public IReadOnlyList<string> LocalMute { get; init; } = [];
    public IReadOnlyList<string> LocalUnmute { get; init; } = [];
}