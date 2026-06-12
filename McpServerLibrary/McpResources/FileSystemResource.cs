using System.ComponentModel;
using System.Text.Json;
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
            name = "media",
            mountPoint = "/media",
            description = $"Media library ({settings.BaseLibraryPath}) — books, audiobooks, and other downloaded media. Read/list focused; treat writes as organisational only. Does NOT support fs_exec."
        });
    }

    [McpServerResource(
        UriTemplate = "filesystem://downloads",
        Name = "Downloads Filesystem",
        MimeType = "application/json")]
    [Description("Active downloads exposed as a filesystem")]
    public string GetDownloadsInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "downloads",
            mountPoint = "/downloads",
            description = "Active torrent downloads. Each download is a directory /downloads/<id>/ with a " +
                          "read-only status.json (state, progress, eta, savePath). Deleting /downloads/<id> " +
                          "cancels the download, removes the torrent task, and cleans up its files. " +
                          "Read-only otherwise; downloads are started with the download_file tool."
        });
    }
}