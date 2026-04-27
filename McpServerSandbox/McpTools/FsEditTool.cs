using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsEditTool(McpSettings settings)
    : TextEditTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_edit")]
    [Description(Description)]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string oldString,
        string newString,
        bool replaceAll = false)
    {
        return ToolResponse.Create(Run(path, oldString, newString, replaceAll));
    }
}
