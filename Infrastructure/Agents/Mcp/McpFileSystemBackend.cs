using System.Text.Json.Nodes;
using Domain.Contracts;
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
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["offset"] = offset,
            ["limit"] = limit
        }, ct);
    }

    public async Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        return await CallToolAsync("fs_create", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
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
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["oldString"] = oldString,
            ["newString"] = newString,
            ["replaceAll"] = replaceAll
        }, ct);
    }

    public async Task<JsonNode> GlobAsync(string basePath, string pattern, string mode, CancellationToken ct)
    {
        return await CallToolAsync("fs_glob", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["basePath"] = basePath,
            ["pattern"] = pattern,
            ["mode"] = mode
        }, ct);
    }

    public async Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, string outputMode, CancellationToken ct)
    {
        return await CallToolAsync("fs_search", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["directoryPath"] = directoryPath,
            ["filePattern"] = filePattern,
            ["maxResults"] = maxResults,
            ["contextLines"] = contextLines,
            ["outputMode"] = outputMode
        }, ct);
    }

    public async Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        return await CallToolAsync("fs_move", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        }, ct);
    }

    public async Task<JsonNode> DeleteAsync(string path, CancellationToken ct)
    {
        return await CallToolAsync("fs_delete", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path
        }, ct);
    }

    private async Task<JsonNode> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(toolName, args, cancellationToken: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The '{filesystemName}' filesystem does not support the '{toolName}' operation. " +
                $"This tool is not available on this filesystem backend.", ex);
        }

        if (result.IsError == true)
        {
            var errorText = string.Join("\n", result.Content
                .OfType<TextContentBlock>()
                .Select(c => c.Text));
            throw new InvalidOperationException(
                $"Error calling '{toolName}' on the '{filesystemName}' filesystem: {errorText}");
        }

        var text = string.Join("\n", result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        return JsonNode.Parse(text)
            ?? throw new InvalidOperationException($"Failed to parse response from {toolName}");
    }
}
