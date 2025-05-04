namespace Domain.DTOs;

public record DownloadItem : SearchResult
{
    public required string SavePath { get; init; }
    public required DownloadStatus Status { get; init; }
}

public enum DownloadStatus
{
    InProgress,
    Completed,
    Paused,
    Failed
}