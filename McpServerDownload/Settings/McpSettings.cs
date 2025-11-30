using JetBrains.Annotations;

namespace McpServerDownload.Settings;

public record McpSettings
{
    public required JackettConfiguration Jackett { get; init; }
    public required QBittorrentConfiguration QBittorrent { get; init; }
    public required string DownloadLocation { get; init; }
    public required RedisConfiguration Redis { get; init; }
}

public record JackettConfiguration
{
    public required string ApiKey { get; [UsedImplicitly] init; }
    public required string ApiUrl { get; [UsedImplicitly] init; }
}

public record QBittorrentConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string UserName { get; [UsedImplicitly] init; }
    public required string Password { get; [UsedImplicitly] init; }
}

public record RedisConfiguration
{
    public required string ConnectionString { get; [UsedImplicitly] init; }
    public int SearchResultsExpiryDays { get; [UsedImplicitly] init; } = 7;
    public int TrackedDownloadsExpiryDays { get; [UsedImplicitly] init; } = 7;
}
