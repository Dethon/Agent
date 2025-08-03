namespace Domain.DTOs;

public record DownloadItem : SearchResult
{
    public required string SavePath { get; init; }
    public required DownloadStatus Status { get; init; }
    public required double Progress { get; init; }
    public required double DownSpeed { get; init; }
    public required double UpSpeed { get; init; }
    public required double Eta { get; init; }
}

public enum DownloadStatus
{
    InProgress,
    Completed,
    Paused,
    Failed
}