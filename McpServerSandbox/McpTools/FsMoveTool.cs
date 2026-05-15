using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_move")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}