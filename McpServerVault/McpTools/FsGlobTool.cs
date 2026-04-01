using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("Search for files or directories matching a glob pattern")]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string pattern,
        string mode = "directories",
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        var globMode = mode.Equals("files", StringComparison.OrdinalIgnoreCase)
            ? GlobMode.Files
            : GlobMode.Directories;

        var effectivePattern = string.IsNullOrEmpty(basePath)
            ? pattern
            : $"{basePath.TrimEnd('/')}/{pattern}";

        return ToolResponse.Create(await Run(effectivePattern, globMode, cancellationToken));
    }
}
