namespace McpServerTextTools.Settings;

public record McpSettings
{
    public required string VaultPath { get; init; }
    public required string[] AllowedExtensions { get; init; }
}