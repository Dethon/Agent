using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.McpTools;

[McpServerToolType]
public class McpMoveTool(IFileSystemClient client, LibraryPathConfig libraryPath) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(string sourcePath, string destinationPath, CancellationToken ct)
    {
        try
        {
            return ToolResponse.Create(await Run(sourcePath, destinationPath, ct));
        }
        catch (Exception ex)
        {
            return ToolResponse.Create(ex);
        }
    }
}