using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsCreateTool(McpSettings settings)
    : TextCreateTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_create")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        string content,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, content, overwrite, createDirectories));
    }
}
