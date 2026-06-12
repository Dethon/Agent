namespace Domain.Tools.Downloads.Vfs;

public enum DownloadNodeKind
{
    Root,
    DownloadDir,
    StatusFile,
    Unknown
}

public sealed record DownloadsNode(DownloadNodeKind Kind, int? Id);

public static class DownloadsPath
{
    public const string StatusFileName = "status.json";

    public static DownloadsNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments switch
        {
            [] => new DownloadsNode(DownloadNodeKind.Root, null),
            [var id] when int.TryParse(id, out var dirId) => new DownloadsNode(DownloadNodeKind.DownloadDir, dirId),
            [var id, StatusFileName] when int.TryParse(id, out var fileId) => new DownloadsNode(DownloadNodeKind.StatusFile, fileId),
            _ => new DownloadsNode(DownloadNodeKind.Unknown, null)
        };
    }
}