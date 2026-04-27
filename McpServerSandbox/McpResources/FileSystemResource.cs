using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(
        UriTemplate = "filesystem://sandbox",
        Name = "Sandbox Filesystem",
        MimeType = "application/json")]
    [Description("Linux sandbox container filesystem with bash + Python execution")]
    public string GetSandboxInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "sandbox",
            mountPoint = "/sandbox",
            description = "Linux sandbox: persistent /home/sandbox_user, ephemeral system dirs, full network, bash + Python via fs_exec"
        });
    }
}
