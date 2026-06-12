namespace Domain.Tools.Downloads.Vfs;

public enum DownloadNodeKind
{
    DownloadDir,
    StatusFile,
    Other
}

public sealed record DownloadsNode(DownloadNodeKind Kind, int? Id);

// Classifies media-filesystem paths against the downloads overlay: downloads/<id> is a
// download directory and downloads/<id>/status.json its virtual status file; everything
// else (including payload files inside a download directory) is plain disk territory.
public static class DownloadsPath
{
    public const string StatusFileName = "status.json";

    public static DownloadsNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [MediaFilesystem.DownloadsSubdir, var id] when int.TryParse(id, out var dirId) =>
                new DownloadsNode(DownloadNodeKind.DownloadDir, dirId),
            [MediaFilesystem.DownloadsSubdir, var id, StatusFileName] when int.TryParse(id, out var fileId) =>
                new DownloadsNode(DownloadNodeKind.StatusFile, fileId),
            _ => new DownloadsNode(DownloadNodeKind.Other, null)
        };
    }
}