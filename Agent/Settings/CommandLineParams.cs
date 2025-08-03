namespace Agent.Settings;

public record CommandLineParams
{
    public bool IsDaemon { get; init; } = true;
    public bool SshMode { get; init; }
    public string? Prompt { get; init; }
    public int WorkersCount { get; init; } = 10;
}