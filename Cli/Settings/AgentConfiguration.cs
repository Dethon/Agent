using JetBrains.Annotations;

namespace Cli.Settings;

public record AgentConfiguration
{
    public required OpenRouterConfiguration OpenRouter { get; init; }
    public required JackettConfiguration Jackett { get; init; }
    public required QBittorrentConfiguration QBittorrent { get; init; }
    public required SshConfiguration Ssh { get; init; }
    public required TelegramConfiguration Telegram { get; init; }
    public required string DownloadLocation { get; init; }
    public required string BaseLibraryPath { get; init; }
}

public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
    public required string Model { get; [UsedImplicitly] init; }
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

public record SshConfiguration
{
    public required string Host { get; [UsedImplicitly] init; }
    public required string UserName { get; [UsedImplicitly] init; }
    public required string KeyPath { get; [UsedImplicitly] init; }
    public required string KeyPass { get; [UsedImplicitly] init; }
}

public record TelegramConfiguration
{
    public required string BotToken { get; [UsedImplicitly] init; }
    public required string[] AllowedUserNames { get; [UsedImplicitly] init; }
}