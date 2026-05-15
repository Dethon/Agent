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
            description = $"Personal Obsidian vault ({settings.VaultPath}) — markdown notes with wikilinks, embeds, frontmatter, and tags; the user edits the same files in Obsidian. Persistent host-mounted directory. Read/write text only (allowed extensions enforced); does NOT support fs_exec. See the Vault Filesystem (Obsidian) prompt for conventions."
        });
    }
}