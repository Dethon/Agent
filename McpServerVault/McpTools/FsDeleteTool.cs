using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsDeleteTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : RemoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_delete")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string path,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
