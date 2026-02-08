using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Source path (absolute or relative to library root)")]
        string sourcePath,
        [Description("Destination path (absolute or relative to library root, must not exist)")]
        string destinationPath,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}
