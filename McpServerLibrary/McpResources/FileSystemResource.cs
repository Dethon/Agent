using System.ComponentModel;
using System.Text.Json;
using Domain.Tools.Downloads.Vfs;
using McpServerLibrary.Settings;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpResources;

[McpServerResourceType]
public class FileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://media",
        Name = "Media Filesystem",
        MimeType = "application/json")]
    [Description("Media library filesystem")]
    public string GetMediaInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = MediaFilesystem.Name,
            mountPoint = MediaFilesystem.MountPoint,
            description = $"Media library ({settings.BaseLibraryPath}) — books, audiobooks, and other downloaded media. " +
                          "Read/list focused; treat writes as organisational only. Does NOT support fs_exec. " +
                          $"Active downloads live under {MediaFilesystem.MountPoint}/{MediaFilesystem.DownloadsSubdir}/<id>/: " +
                          "a virtual read-only status.json reports live state/progress/eta, and deleting the <id> " +
                          "directory cancels the download and cleans up its files."
        });
    }
}