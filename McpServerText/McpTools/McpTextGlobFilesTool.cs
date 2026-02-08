using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class McpTextGlobFilesTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Glob pattern relative to the library root. Examples: **/*.md, notes/*, **/*.txt")]
        string pattern,
        [Description("Search mode: 'directories' (default) lists matching directories for exploration, 'files' lists matching files (capped at 200 results).")]
        string mode = "directories",
        CancellationToken cancellationToken = default)
    {
        var globMode = ParseMode(mode);
        return ToolResponse.Create(await Run(pattern, globMode, cancellationToken));
    }

    private static GlobMode ParseMode(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "directories" => GlobMode.Directories,
            "files" => GlobMode.Files,
            _ => throw new ArgumentException($"Invalid mode '{mode}'. Use 'directories' or 'files'.", nameof(mode))
        };
}
