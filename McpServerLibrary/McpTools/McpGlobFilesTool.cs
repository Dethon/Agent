using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpGlobFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Glob pattern relative to the library root. Examples: **/*.pdf, books/*, **/*.mkv")]
        string pattern,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(pattern, cancellationToken));
    }
}
