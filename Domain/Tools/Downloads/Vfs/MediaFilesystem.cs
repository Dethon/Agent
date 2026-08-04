namespace Domain.Tools.Downloads.Vfs;

// Where a download lands, as the agent addresses it. The mount's identity is the backend's — see
// MediaLibraryDiskFileSystem — and what is left here is the one thing that is not derivable from
// it: the compose volumes pin the physical identity ${DATA_PATH}/downloads == <media root>/downloads,
// so the downloads subdir is a constant, not configuration.
public static class MediaFilesystem
{
    public const string DownloadsSubdir = "downloads";

    public static string DownloadsDir => $"/{MediaLibraryDiskFileSystem.Name}/{DownloadsSubdir}";

    public static string AgentDownloadDir(int id) => $"{DownloadsDir}/{id}";
}