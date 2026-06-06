namespace McpChannelVoice.Settings;

public record FollowUpSettings
{
    // Master switch. When false the channel behaves as before: the transcript is sent
    // immediately after each utterance and the satellite re-arms wake every turn.
    public bool Enabled { get; init; } = true;

    // How long the re-opened mic waits for the user to START speaking before the
    // conversation falls back to wake-required mode.
    public int WindowMs { get; init; } = 7000;

    // Echo guard: discard mic after playback for this long before opening the window,
    // letting speaker decay / room reverb settle.
    public int PlaybackTailMs { get; init; } = 400;

    // Play the listening earcon before each follow-up window.
    public bool Chime { get; init; } = true;

    // Runaway cap: fall back to wake after this many consecutive follow-up turns.
    public int MaxTurns { get; init; } = 8;
}