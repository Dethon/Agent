using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class FileSystemToolFeature(IVirtualFileSystemRegistry registry) : IDomainToolFeature
{
    private const string Feature = "filesystem";

    public static readonly IReadOnlySet<string> AllToolKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        VfsTextReadTool.Key, VfsTextCreateTool.Key, VfsTextEditTool.Key,
        VfsGlobFilesTool.Key, VfsTextSearchTool.Key, VfsMoveTool.Key, VfsCopyTool.Key, VfsRemoveTool.Key,
        VfsExecTool.Key, VfsFileInfoTool.Key
    };

    public string FeatureName => Feature;

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            (VfsTextReadTool.Key, () => AIFunctionFactory.Create(new VfsTextReadTool(registry).RunAsync, name: $"domain__{Feature}__{VfsTextReadTool.Name}")),
            (VfsTextCreateTool.Key, () => AIFunctionFactory.Create(new VfsTextCreateTool(registry).RunAsync, name: $"domain__{Feature}__{VfsTextCreateTool.Name}")),
            (VfsTextEditTool.Key, () => AIFunctionFactory.Create(new VfsTextEditTool(registry).RunAsync, name: $"domain__{Feature}__{VfsTextEditTool.Name}")),
            (VfsGlobFilesTool.Key, () => AIFunctionFactory.Create(new VfsGlobFilesTool(registry).RunAsync, name: $"domain__{Feature}__{VfsGlobFilesTool.Name}")),
            (VfsTextSearchTool.Key, () => AIFunctionFactory.Create(new VfsTextSearchTool(registry).RunAsync, name: $"domain__{Feature}__{VfsTextSearchTool.Name}")),
            (VfsMoveTool.Key, () => AIFunctionFactory.Create(new VfsMoveTool(registry).RunAsync, name: $"domain__{Feature}__{VfsMoveTool.Name}")),
            (VfsCopyTool.Key, () => AIFunctionFactory.Create(new VfsCopyTool(registry).RunAsync, name: $"domain__{Feature}__{VfsCopyTool.Name}")),
            (VfsRemoveTool.Key, () => AIFunctionFactory.Create(new VfsRemoveTool(registry).RunAsync, name: $"domain__{Feature}__{VfsRemoveTool.Name}")),
            (VfsExecTool.Key, () => AIFunctionFactory.Create(new VfsExecTool(registry).RunAsync, name: $"domain__{Feature}__{VfsExecTool.Name}")),
            (VfsFileInfoTool.Key, () => AIFunctionFactory.Create(new VfsFileInfoTool(registry).RunAsync, name: $"domain__{Feature}__{VfsFileInfoTool.Name}")),
        };

        return tools
            .Where(t => config.EnabledTools is null || config.EnabledTools.Contains(t.Key))
            .Select(t => t.Factory());
    }

    private string? BuildPrompt()
    {
        var mounts = registry.GetMounts();
        if (mounts.Count == 0)
        {
            return null;
        }

        var mountList = string.Join("\n", mounts.Select(m => $"- `{m.MountPoint}` — {m.Description}"));
        return $$"""
            ## Available Filesystems

            All `domain__filesystem__*` tool paths must start with one of these mount prefixes. Pick the mount whose description matches your task; don't scatter related files across mounts.
            {{mountList}}

            ### How capabilities work

            Each mount is backed by a different MCP server, and **each backend implements only the operations that make sense for it** — read-only mounts won't accept writes, non-shell mounts won't accept `exec`, and so on. The mount's description above is your primary signal for what it supports.

            If you call a tool the backend doesn't implement, the response is a structured error envelope (`{"ok": false, "errorCode": "unsupported_operation", "message": "...", "retryable": false, "hint": "..."}`) — treat it as data, not as an exception. Use it as a hint to pick a different mount or a different operation, not as a reason to retry.

            ### Cross-mount reminders

            - Each mount is its own backend. Tools see only the filesystem of the mount you target — they cannot reach files on a different mount. If you need data from one mount available to a command on another (e.g. for `exec`), copy it across first.
            - `move` and `copy` accept source and destination on different mounts and handle the transfer natively (streaming for cross-FS, recursing into directories) — prefer a single `copy`/`move` call over reading on one mount and creating on another.
            - Paths are virtual: always include the mount prefix. Don't pass bare `/home/...` or `/notes/...` — start with one of the mount points listed above.
            """;
    }
}