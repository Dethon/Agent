using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    DownloadsFileSystem downloads) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        string? filesystem = null,
        CancellationToken cancellationToken = default)
        => filesystem == downloads.FilesystemName
            ? ToolResponse.Create(await downloads.GlobAsync(basePath, pattern, cancellationToken))
            : ToolResponse.Create(await Run(pattern, cancellationToken, basePath));
}