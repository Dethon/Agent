using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class DomainToolRegistry(IEnumerable<IDomainToolFeature> features) : IDomainToolRegistry
{
    private readonly Dictionary<string, IDomainToolFeature> _features =
        features.ToDictionary(f => f.FeatureName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config)
    {
        return enabledFeatures
            .Where(name => _features.ContainsKey(name))
            .SelectMany(name => _features[name].GetTools(config));
    }

    public IEnumerable<string> GetPromptsForFeatures(IEnumerable<string> enabledFeatures)
    {
        return enabledFeatures
            .Where(name => _features.ContainsKey(name))
            .Select(name => _features[name].Prompt)
            .Where(prompt => prompt is not null)!;
    }
}
