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
            description = $"Media library ({settings.BaseLibraryPath})"
        });
    }
}
