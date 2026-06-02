using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsSearchTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search the text content of queued documents.")]
    public async Task<CallToolResult> McpRun(
        string query, bool regex = false, string? path = null, string? directoryPath = null,
        string? filePattern = "*", int maxResults = 50, int contextLines = 0,
        VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content, CancellationToken ct = default)
        => ToolResponse.Create(await fs.SearchAsync(query, regex, path, directoryPath, filePattern!, maxResults, contextLines, outputMode, ct));
}