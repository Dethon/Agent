using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerOrganize.McpTools;

[McpServerToolType]
public class McpMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    ILogger<McpMoveTool> logger) : MoveTool(client, libraryPath)
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
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }
}