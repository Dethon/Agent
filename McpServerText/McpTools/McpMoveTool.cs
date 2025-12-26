using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    ILogger<McpMoveTool> logger) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Absolute source path (from ListDirectories/ListFiles)")]
        string sourcePath,
        [Description("Absolute destination path (must not exist)")]
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
        }
        catch (Exception ex)
        {
            if (logger.IsEnabled(LogLevel.Error))
            {
                logger.LogError(ex, "Error in {ToolName} tool", Name);
            }

            return ToolResponse.Create(ex);
        }
    }
}