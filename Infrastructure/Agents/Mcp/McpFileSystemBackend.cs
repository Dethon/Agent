using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal sealed class McpFileSystemBackend(McpClient client, string filesystemName) : IFileSystemBackend
{
    public string FilesystemName => filesystemName;

    public async Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        return await CallToolAsync("fs_read", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["offset"] = offset,
            ["limit"] = limit
        }, ct);
    }

    public async Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        return await CallToolAsync("fs_create", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = content,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);
    }

    public async Task<JsonNode> EditAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct)
    {
        return await CallToolAsync("fs_edit", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["oldString"] = oldString,
            ["newString"] = newString,
            ["replaceAll"] = replaceAll
        }, ct);
    }

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, VfsGlobMode mode, CancellationToken ct)
    {
        return await CallToolAsync("fs_glob", new Dictionary<string, object?>
        {
            ["basePath"] = basePath,
            ["pattern"] = pattern,
            ["mode"] = mode.ToString()
        }, ct);
    }

    public async Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        return await CallToolAsync("fs_search", new Dictionary<string, object?>
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["directoryPath"] = directoryPath,
            ["filePattern"] = filePattern,
            ["maxResults"] = maxResults,
            ["contextLines"] = contextLines,
            ["outputMode"] = outputMode.ToString()
        }, ct);
    }

    public async Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        return await CallToolAsync("fs_move", new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        }, ct);
    }

    public async Task<JsonNode> DeleteAsync(string path, CancellationToken ct)
    {
        return await CallToolAsync("fs_delete", new Dictionary<string, object?>
        {
            ["path"] = path
        }, ct);
    }

    public async Task<JsonNode> InfoAsync(string path, CancellationToken ct)
    {
        return await CallToolAsync("fs_info", new Dictionary<string, object?>
        {
            ["path"] = path
        }, ct);
    }

    public async Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        return await CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["command"] = command,
            ["timeoutSeconds"] = timeoutSeconds
        }, ct);
    }

    private async Task<JsonNode> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Client-side dispatch failure: the call never reached the server's
            // AddCallToolFilter (e.g. tool not registered, schema mismatch, transport error).
            // Server-side rejections arrive as result.IsError=true with an envelope payload
            // and are handled below.
            return ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                $"The '{filesystemName}' filesystem does not support the '{toolName}' operation. " +
                $"This tool is not available on this filesystem backend. Details: {ex.Message}",
                retryable: false,
                hint: "Pick a different mount or a different operation.");
        }

        var text = string.Join("\n", result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        var parsed = JsonNode.Parse(text);

        if (result.IsError == true)
        {
            return parsed is JsonObject envelope && envelope["ok"] is JsonValue
                ? envelope
                : ToolError.Create(
                    ToolError.Codes.InternalError,
                    $"Error calling '{toolName}' on the '{filesystemName}' filesystem: {text}",
                    retryable: false);
        }

        return parsed
            ?? throw new InvalidOperationException($"Failed to parse response from {toolName}");
    }
}
