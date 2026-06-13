namespace Domain.DTOs;

public record FileSystemMount(string Name, string MountPoint, string Description)
{
    // Domain-tool leaf names (text_read, glob, exec, …) the backing MCP server actually exposes,
    // derived at discovery from its advertised fs_* tool set. Empty when unknown.
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}