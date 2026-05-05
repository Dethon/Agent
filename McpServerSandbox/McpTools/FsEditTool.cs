using System.ComponentModel;
using Domain.DTOs;
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
        string path,
        IReadOnlyList<TextEdit> edits)
    {
        return ToolResponse.Create(Run(path, edits));
    }
}
