using System.ComponentModel;
using System.Text.Json;
using McpServerText.Settings;
using ModelContextProtocol.Server;

namespace McpServerText.McpResources;

[McpServerResourceType]
public class FileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://library",
        Name = "Library Filesystem",
        MimeType = "application/json")]
    [Description("Personal document library filesystem")]
    public string GetLibraryInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "library",
            mountPoint = "/library",
            description = $"Personal document library ({settings.VaultPath})"
        });
    }
}
