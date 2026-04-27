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
        VfsGlobFilesTool.Key, VfsTextSearchTool.Key, VfsMoveTool.Key, VfsRemoveTool.Key,
        VfsExecTool.Key
    };

    public string FeatureName => Feature;

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            (VfsTextReadTool.Key, () => AIFunctionFactory.Create(new VfsTextReadTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextReadTool.Name}")),
            (VfsTextCreateTool.Key, () => AIFunctionFactory.Create(new VfsTextCreateTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextCreateTool.Name}")),
            (VfsTextEditTool.Key, () => AIFunctionFactory.Create(new VfsTextEditTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextEditTool.Name}")),
            (VfsGlobFilesTool.Key, () => AIFunctionFactory.Create(new VfsGlobFilesTool(registry).RunAsync, name: $"domain:{Feature}:{VfsGlobFilesTool.Name}")),
            (VfsTextSearchTool.Key, () => AIFunctionFactory.Create(new VfsTextSearchTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextSearchTool.Name}")),
            (VfsMoveTool.Key, () => AIFunctionFactory.Create(new VfsMoveTool(registry).RunAsync, name: $"domain:{Feature}:{VfsMoveTool.Name}")),
            (VfsRemoveTool.Key, () => AIFunctionFactory.Create(new VfsRemoveTool(registry).RunAsync, name: $"domain:{Feature}:{VfsRemoveTool.Name}")),
            (VfsExecTool.Key, () => AIFunctionFactory.Create(new VfsExecTool(registry).RunAsync, name: $"domain:{Feature}:{VfsExecTool.Name}")),
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

        var mountList = string.Join("\n", mounts.Select(m => $"- {m.MountPoint} — {m.Description}"));
        return $"## Available Filesystems\n\nAll file tool paths must start with one of these prefixes:\n{mountList}";
    }
}
