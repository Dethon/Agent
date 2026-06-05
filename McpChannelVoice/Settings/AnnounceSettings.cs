namespace McpChannelVoice.Settings;

public record AnnounceSettings
{
    public bool Enabled { get; init; } = true;
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public int MaxTextLength { get; init; } = 1000;
    public AnnouncePriorityDefault DefaultPriority { get; init; } = AnnouncePriorityDefault.Normal;
}

public enum AnnouncePriorityDefault { Low, Normal, High }