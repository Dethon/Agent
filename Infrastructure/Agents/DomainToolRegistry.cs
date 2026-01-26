using Domain.Contracts;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class DomainToolRegistry(IEnumerable<IDomainToolFeature> features) : IDomainToolRegistry
{
    private readonly Dictionary<string, IDomainToolFeature> _features =
        features.ToDictionary(f => f.FeatureName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures)
    {
        return enabledFeatures
            .Where(name => _features.ContainsKey(name))
            .SelectMany(name => _features[name].GetTools());
    }
}
