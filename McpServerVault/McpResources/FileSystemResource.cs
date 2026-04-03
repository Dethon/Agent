using System.ComponentModel;
using System.Text.Json;
using McpServerVault.Settings;
using ModelContextProtocol.Server;

namespace McpServerVault.McpResources;

[McpServerResourceType]
public class FileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://vault",
        Name = "Vault Filesystem",
        MimeType = "application/json")]
    [Description("Personal document library filesystem")]
    public string GetLibraryInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "vault",
            mountPoint = "/vault",
            description = $"Personal document vault ({settings.VaultPath})"
        });
    }
}
