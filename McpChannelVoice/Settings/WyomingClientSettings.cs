namespace McpChannelVoice.Settings;

public record WyomingClientSettings
{
    // Delay before re-dialing a satellite after its connection drops.
    public int ReconnectDelaySeconds { get; init; } = 5;

    // End-of-utterance detection (see SilenceGate). Tuned for 16 kHz/16-bit mono.
    public double SilenceRmsThreshold { get; init; } = 500;
    public int TrailingSilenceMs { get; init; } = 800;
    public int MaxUtteranceMs { get; init; } = 15_000;
    public int MinSpeechMs { get; init; } = 200;
}