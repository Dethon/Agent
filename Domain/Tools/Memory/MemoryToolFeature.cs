using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Memory;

public class MemoryToolFeature(IMemoryStore store) : IDomainToolFeature
{
    private const string Feature = "memory";

    public string FeatureName => Feature;

    public string? Prompt => MemoryPrompts.FeatureSystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var forgetTool = new MemoryForgetTool(store);
        yield return AIFunctionFactory.Create(
            forgetTool.Run,
            name: $"domain:{Feature}:{MemoryForgetTool.Name}",
            description: MemoryForgetTool.Description);
    }
}
