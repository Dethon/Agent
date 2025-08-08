using JetBrains.Annotations;

namespace McpServer.Download.Settings;

public record McpSettings
{
    public required SshConfiguration Ssh { get; init; }
    public required string BaseLibraryPath { get; init; }

    public required string DownloadLocation { get; init; }
}

public record SshConfiguration
{
    public required string Host { get; [UsedImplicitly] init; }
    public required string UserName { get; [UsedImplicitly] init; }
    public required string KeyPath { get; [UsedImplicitly] init; }
    public required string KeyPass { get; [UsedImplicitly] init; }
}