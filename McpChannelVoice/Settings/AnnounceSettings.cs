namespace McpChannelVoice.Settings;

public record AnnounceSettings
{
    public bool Enabled { get; init; } = true;
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public int MaxTextLength { get; init; } = 50000;
    public InsistentDefaults Insistent { get; init; } = new();
    public EscalationSettings Escalation { get; init; } = new();
}

public record EscalationSettings
{
    // HA webhook POSTed when an ALARM caps out unacknowledged (timers never escalate).
    // Null/empty disables escalation.
    public string? WebhookUrl { get; init; }
}

public record InsistentDefaults
{
    public int GapSeconds { get; init; } = 15;
    public int MaxRepeats { get; init; } = 12;
    public int? MaxDurationSeconds { get; init; }
    // Round-1 playback gain in percent, ramping linearly to 100 by RampRounds. 100 disables the ramp.
    public int RampStartPercent { get; init; } = 50;
    public int RampRounds { get; init; } = 4;
}