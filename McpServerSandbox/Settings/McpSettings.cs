namespace McpServerSandbox.Settings;

public record McpSettings
{
    public required string ContainerRoot { get; init; }
    public required string HomeDir { get; init; }
    public required int DefaultTimeoutSeconds { get; init; }
    public required int MaxTimeoutSeconds { get; init; }
    public required int OutputCapBytes { get; init; }
    public required string[] AllowedExtensions { get; init; }
}