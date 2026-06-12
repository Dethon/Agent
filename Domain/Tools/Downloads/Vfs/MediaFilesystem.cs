namespace Domain.Tools.Downloads.Vfs;

// Single source for the library server's filesystem identity. The compose volumes pin the
// physical identity ${DATA_PATH}/downloads == <media root>/downloads, so the downloads
// subdir is a constant, not configuration.
public static class MediaFilesystem
{
    public const string Name = "media";
    public const string MountPoint = "/media";
    public const string DownloadsSubdir = "downloads";

    public static string AgentDownloadDir(int id) => $"{MountPoint}/{DownloadsSubdir}/{id}";
}