namespace Domain.DTOs;

public record DownloadItem : SearchResult
{
    public required string SavePath { get; init; }
    public required DownloadStatus Status { get; init; }
    public required int Progress { get; init; }
    public required long DownSpeed { get; init; }
    public required long UpSpeed { get; init; }
    public required long Eta { get; init; }
}

public enum DownloadStatus
{
    InProgress,
    Completed,
    Paused,
    Failed
}