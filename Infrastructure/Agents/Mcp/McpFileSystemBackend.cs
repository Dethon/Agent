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

    public async Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        return await CallToolAsync("fs_edit", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["edits"] = edits.Select(e => new Dictionary<string, object?>
            {
                ["oldString"] = e.OldString,
                ["newString"] = e.NewString,
                ["replaceAll"] = e.ReplaceAll
            }).ToList()
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

    public async Task<JsonNode> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        return await CallToolAsync("fs_copy", new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);
    }

    public async Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
        var buffer = new MemoryStream();
        long offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var node = await CallToolAsync("fs_blob_read", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["offset"] = offset,
                ["length"] = chunkSize
            }, ct);

            if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
            {
                throw new IOException($"fs_blob_read failed: {obj["message"]?.GetValue<string>()}");
            }

            var b64 = node["contentBase64"]!.GetValue<string>();
            var bytes = Convert.FromBase64String(b64);
            buffer.Write(bytes, 0, bytes.Length);
            offset += bytes.Length;

            if (node["eof"]!.GetValue<bool>())
            {
                break;
            }

            if (bytes.Length == 0)
            {
                break;
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    public async Task WriteFromStreamAsync(string path, Stream content,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
        var buffer = new byte[chunkSize];
        long offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await content.ReadAsync(buffer.AsMemory(0, chunkSize), ct);
            if (read == 0)
            {
                break;
            }

            var chunk = read == chunkSize ? buffer : buffer[..read];
            var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = Convert.ToBase64String(chunk),
                ["offset"] = offset,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }, ct);

            if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
            {
                throw new IOException($"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
            }

            offset += read;
        }

        if (offset == 0)
        {
            var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = "",
                ["offset"] = 0L,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }, ct);

            if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
            {
                throw new IOException($"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
            }
        }
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
