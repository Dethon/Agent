namespace Domain.DTOs;

public record DownloadItem : SearchResult
{
    public required string SavePath { get; init; }
    public required DownloadState State { get; init; }
    public required double Progress { get; init; }
    public required double DownSpeed { get; init; }
    public required double UpSpeed { get; init; }
    public required double Eta { get; init; }
}

public record DownloadStatus
{
    public required int Id { get; init; }
    public required DownloadState State { get; init; }
    public required double Progress { get; init; }
    public required double DownSpeed { get; init; }
    public required double UpSpeed { get; init; }
    public required double Eta { get; init; }

    public DownloadStatus(DownloadItem item)
    {
        Id = item.Id;
        State = item.State;
        Progress = item.Progress;
        DownSpeed = item.DownSpeed;
        UpSpeed = item.UpSpeed;
        Eta = item.Eta;
    }
}

public enum DownloadState
{
    InProgress,
    Completed,
    Paused,
    Failed
}