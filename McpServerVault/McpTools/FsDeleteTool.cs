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
    [Description("Delete a file or directory (move to trash)")]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string path,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
