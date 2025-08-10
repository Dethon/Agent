using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.McpTools;

[McpServerToolType]
public class McpListFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath) :
    ListFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(string path, CancellationToken cancellationToken)
    {
        try
        {
            return ToolResponse.Create(await Run(path, cancellationToken));
        }
        catch (Exception ex)
        {
            return ToolResponse.Create(ex);
        }
    }
}