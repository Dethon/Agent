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
public class McpTextListFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    ILogger<McpTextListFilesTool> logger) : ListFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Absolute path to the directory")]
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return ToolResponse.Create(await Run(path, cancellationToken));
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