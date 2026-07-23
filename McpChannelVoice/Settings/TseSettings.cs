namespace McpChannelVoice.Settings;

public enum TseMode
{
    Off,
    Auto,
    Always
}

// Target-speaker extraction (spec: docs/superpowers/specs/2026-07-22-tse-live-integration-design.md).
// Mode is the kill switch: Off = decorator not even wrapped (restart to change). Auto extracts only
// when the gate produced a target AND the capture's pre-speech floor is at/above
// NoiseFloorThreshold; Always extracts whenever a target exists (diagnostic).
public record TseSettings
{
    public TseMode Mode { get; init; } = TseMode.Off;
    public string Endpoint { get; init; } = "http://tse-extractor:9098";
    public int TimeoutMs { get; init; } = 90000;
    public double NoiseFloorThreshold { get; init; } = 400;
    // Opt-in audio audit ring: null/empty disables. Each extraction writes
    // mixture.wav + extracted.wav + meta.json; oldest pruned beyond AuditMaxPairs.
    public string? AuditDir { get; init; }
    public int AuditMaxPairs { get; init; } = 50;
}