using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

internal class McpFileSystemBackend(McpClient client, string filesystemName, ILogger? logger = null) : IFileSystemBackend
{
    public string FilesystemName => filesystemName;

    public Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        CallTypedAsync<FsReadResult>("fs_read", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["offset"] = offset,
            ["limit"] = limit
        }, ct);

    public Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
        CallTypedAsync<FsCreateResult>("fs_create", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = content,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);

    public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        CallTypedAsync<FsEditResult>("fs_edit", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["edits"] = edits.Select(e => new Dictionary<string, object?>
            {
                ["oldString"] = e.OldString,
                ["newString"] = e.NewString,
                ["replaceAll"] = e.ReplaceAll
            }).ToList()
        }, ct);

    public Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
        CallTypedAsync<FsGlobResult>("fs_glob", new Dictionary<string, object?>
        {
            ["basePath"] = basePath,
            ["pattern"] = pattern
        }, ct);

    public Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        CallTypedAsync<FsSearchResult>("fs_search", new Dictionary<string, object?>
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

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        CallTypedAsync<FsMoveResult>("fs_move", new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        }, ct);

    public Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        CallTypedAsync<FsRemoveResult>("fs_delete", new Dictionary<string, object?>
        {
            ["path"] = path
        }, ct);

    public Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        CallTypedAsync<FsInfoResult>("fs_info", new Dictionary<string, object?>
        {
            ["path"] = path
        }, ct);

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        CallTypedAsync<FsExecResult>("fs_exec", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["command"] = command,
            ["timeoutSeconds"] = timeoutSeconds
        }, ct);

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        CallTypedAsync<FsCopyResult>("fs_copy", new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
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

            var blobError = ToolErrorResult.FromEnvelope(node);
            if (blobError is not null)
            {
                throw new IOException($"fs_blob_read failed: {blobError.Message}");
            }

            var bytes = Convert.FromBase64String(node["contentBase64"]!.GetValue<string>());
            if (bytes.Length > 0)
            {
                offset += bytes.Length;
                yield return bytes;
            }

            if (node["eof"]!.GetValue<bool>())
            {
                yield break;
            }

            if (bytes.Length == 0)
            {
                // Defensive: server reported !eof but sent nothing — break to avoid infinite loop.
                yield break;
            }
        }
    }

    public async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        long offset = 0;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = Convert.ToBase64String(chunk.Span),
                ["offset"] = offset,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }, ct);

            var blobError = ToolErrorResult.FromEnvelope(node);
            if (blobError is not null)
            {
                throw new IOException($"fs_blob_write failed: {blobError.Message}");
            }

            offset += chunk.Length;
        }

        if (offset == 0)
        {
            // Empty source: still create the file with zero bytes.
            var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = "",
                ["offset"] = 0L,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }, ct);

            var blobError = ToolErrorResult.FromEnvelope(node);
            if (blobError is not null)
            {
                throw new IOException($"fs_blob_write failed: {blobError.Message}");
            }
        }

        return offset;
    }

    private async Task<FsResult<T>> CallTypedAsync<T>(
        string toolName, Dictionary<string, object?> args, CancellationToken ct) where T : class
    {
        var node = await CallToolAsync(toolName, args, ct);

        var error = ToolErrorResult.FromEnvelope(node);
        if (error is not null)
        {
            return new FsResult<T>.Err(error);
        }

        var value = node.Deserialize<T>(FsResultContract.ValidationOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize '{toolName}' payload.");
        return new FsResult<T>.Ok(value);
    }

    protected internal virtual async Task<JsonNode> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct)
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
            return ToolErrorResult.IsErrorEnvelope(parsed)
                ? parsed!
                : ToolError.Create(
                    ToolError.Codes.InternalError,
                    $"Error calling '{toolName}' on the '{filesystemName}' filesystem: {text}",
                    retryable: false);
        }

        var node = parsed
            ?? throw new InvalidOperationException($"Failed to parse response from {toolName}");

        if (!FsResultContract.TryValidate(toolName, node, out var validationError))
        {
            logger?.LogWarning(
                "Filesystem '{Filesystem}' returned a malformed '{Tool}' payload: {Error}",
                filesystemName, toolName, validationError);

            return ToolError.Create(
                ToolError.Codes.InternalError,
                $"The '{filesystemName}' filesystem returned a malformed '{toolName}' payload " +
                "that does not match the expected schema.",
                retryable: false,
                hint: "This is a backend bug; the payload was rejected to protect the conversation.");
        }

        return node;
    }
}