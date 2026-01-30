using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpListDirectoriesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : ListDirectoriesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(cancellationToken));
    }
}
