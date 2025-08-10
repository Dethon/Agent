namespace Agent.Settings;

public record CommandLineParams
{
    public int WorkersCount { get; init; } = 10;
}