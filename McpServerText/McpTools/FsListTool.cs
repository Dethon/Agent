using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Tools.Config;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerText.McpTools;

[McpServerToolType]
public class FsListTool(LibraryPathConfig libraryPath)
{
    [McpServerTool(Name = "fs_list")]
    [Description("List directory contents (non-recursive)")]
    public CallToolResult McpRun(
        string filesystem,
        string path = "")
    {
        var fullPath = string.IsNullOrEmpty(path)
            ? libraryPath.BaseLibraryPath
            : Path.GetFullPath(Path.Combine(libraryPath.BaseLibraryPath, path));

        var normalizedBase = libraryPath.BaseLibraryPath.TrimEnd(Path.DirectorySeparatorChar);
        var normalizedFull = fullPath.TrimEnd(Path.DirectorySeparatorChar);
        if (!normalizedFull.Equals(normalizedBase, StringComparison.OrdinalIgnoreCase)
            && !normalizedFull.StartsWith(normalizedBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access denied: path must be within library directory");

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var dirs = Directory.GetDirectories(fullPath)
            .Select(dir => (JsonNode)new JsonObject
            {
                ["name"] = Path.GetFileName(dir),
                ["type"] = "directory"
            });

        var files = Directory.GetFiles(fullPath)
            .Select(file => new FileInfo(file))
            .Select(info => (JsonNode)new JsonObject
            {
                ["name"] = info.Name,
                ["type"] = "file",
                ["size"] = FormatSize(info.Length)
            });

        var entries = new JsonArray([.. dirs.Concat(files)]);

        return ToolResponse.Create(new JsonObject
        {
            ["path"] = path,
            ["entries"] = entries
        });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1}MB"
    };
}
