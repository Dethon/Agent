namespace McpServer.CommandRunner.Settings;

public record McpSettings
{
    public required string WorkingDirectory { get; init; }
}