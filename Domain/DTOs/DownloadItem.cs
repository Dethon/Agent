using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record DownloadItem : SearchResult
{
    public required string SavePath { get; init; }
    public required DownloadState State { get; init; }
    public required double Progress { get; init; }
    public required double DownSpeed { get; init; }
    public required double UpSpeed { get; init; }
    public required double Eta { get; init; }
}

[PublicAPI]
public record DownloadStatus
{
    public int Id { get; init; }
    public string Title { get; init; }
    public DownloadState State { get; init; }

    public double Progress { get; init; }
    public double ProgressPercent => Math.Round(Progress * 100.0, 2);
    public string ProgressText => $"{ProgressPercent:0.##}%";

    public double DownSpeed { get; init; }
    public double UpSpeed { get; init; }
    public double Eta { get; init; }
    public long Seeders { get; init; }
    public long Peers { get; init; }

    public DownloadStatus(DownloadItem item)
    {
        Id = item.Id;
        Title = item.Title;
        State = item.State;
        Progress = item.Progress;
        DownSpeed = item.DownSpeed;
        UpSpeed = item.UpSpeed;
        Eta = item.Eta;
        Seeders = item.Seeders ?? 0;
        Peers = item.Peers ?? 0;
    }
}

public enum DownloadState
{
    InProgress,
    Completed,
    Paused,
    Failed
}