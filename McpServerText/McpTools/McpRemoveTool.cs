using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpRemoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : RemoveTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Path to the file or directory (absolute or relative to library root)")]
        string path,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
