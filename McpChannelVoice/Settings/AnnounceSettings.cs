namespace McpChannelVoice.Settings;

public record AnnounceSettings
{
    public bool Enabled { get; init; } = true;
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public int MaxTextLength { get; init; } = 50000;
    public InsistentDefaults Insistent { get; init; } = new();
}

public record InsistentDefaults
{
    public int GapSeconds { get; init; } = 30;
    public int MaxRepeats { get; init; } = 5;
    public int? MaxDurationSeconds { get; init; }
}