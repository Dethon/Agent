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
}
