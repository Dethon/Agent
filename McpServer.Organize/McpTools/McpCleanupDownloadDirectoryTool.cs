using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Organize.McpTools;

[McpServerToolType]
public class McpCleanupDownloadDirectoryTool(IFileSystemClient fileSystemClient, DownloadPathConfig downloadPath) :
    CleanupDownloadDirectoryTool(fileSystemClient, downloadPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(int downloadId, CancellationToken cancellationToken)
    {
        try
        {
            return ToolResponse.Create(await Run(downloadId, cancellationToken));
        }
        catch (Exception ex)
        {
            return ToolResponse.Create(ex);
        }
    }
}