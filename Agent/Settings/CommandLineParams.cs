namespace Agent.Settings;

public record CommandLineParams
{
    public bool SshMode { get; init; }
    public int WorkersCount { get; init; } = 10;
}