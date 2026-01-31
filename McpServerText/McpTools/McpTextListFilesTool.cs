using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextListFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : ListFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Absolute path to the directory")]
        string path,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
