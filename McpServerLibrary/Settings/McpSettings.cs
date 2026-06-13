using JetBrains.Annotations;

namespace McpServerLibrary.Settings;

public record McpSettings
{
    public required JackettConfiguration Jackett { get; init; }
    public required QBittorrentConfiguration QBittorrent { get; init; }
    public required string DownloadLocation { get; init; }
    public required string BaseLibraryPath { get; init; }
    public required string RedisConnectionString { get; init; }
    public int CompletionPollSeconds { get; init; } = 5;
}

public record JackettConfiguration
{
    public required string ApiKey { get; [UsedImplicitly] init; }
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public int SearchDeadlineSeconds { get; [UsedImplicitly] init; } = 10;
}

public record QBittorrentConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string UserName { get; [UsedImplicitly] init; }
    public required string Password { get; [UsedImplicitly] init; }
}