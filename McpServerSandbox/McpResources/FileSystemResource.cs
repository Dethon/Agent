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
            description = "Linux sandbox container — supports command execution via fs_exec (bash, python3, pip, git, curl, jq). Persistent /home/sandbox_user (named volume), ephemeral system dirs, full outbound network, no inbound ports. See the Sandbox Filesystem prompt for limits."
        });
    }
}