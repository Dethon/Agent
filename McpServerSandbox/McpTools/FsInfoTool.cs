using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsInfoTool(McpSettings settings) : FileInfoTool(settings.ContainerRoot)
{
    [McpServerTool(Name = "fs_info")]
    [Description(Description)]
    public CallToolResult McpRun(string path)
    {
        return ToolResponse.Create(Run(path));
    }
}