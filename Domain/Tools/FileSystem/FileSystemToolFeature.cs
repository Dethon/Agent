using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem;

public class FileSystemToolFeature(IVirtualFileSystemRegistry registry) : IDomainToolFeature
{
    private const string Feature = "filesystem";

    public string FeatureName => Feature;

    public string? Prompt => BuildPrompt();

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            (TextReadTool.Key, () => AIFunctionFactory.Create(new TextReadTool(registry).RunAsync, name: $"domain:{Feature}:{TextReadTool.Name}")),
            (TextCreateTool.Key, () => AIFunctionFactory.Create(new TextCreateTool(registry).RunAsync, name: $"domain:{Feature}:{TextCreateTool.Name}")),
            (TextEditTool.Key, () => AIFunctionFactory.Create(new TextEditTool(registry).RunAsync, name: $"domain:{Feature}:{TextEditTool.Name}")),
            (GlobFilesTool.Key, () => AIFunctionFactory.Create(new GlobFilesTool(registry).RunAsync, name: $"domain:{Feature}:{GlobFilesTool.Name}")),
            (TextSearchTool.Key, () => AIFunctionFactory.Create(new TextSearchTool(registry).RunAsync, name: $"domain:{Feature}:{TextSearchTool.Name}")),
            (MoveTool.Key, () => AIFunctionFactory.Create(new MoveTool(registry).RunAsync, name: $"domain:{Feature}:{MoveTool.Name}")),
            (RemoveTool.Key, () => AIFunctionFactory.Create(new RemoveTool(registry).RunAsync, name: $"domain:{Feature}:{RemoveTool.Name}")),
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
