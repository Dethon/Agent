using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolRegistry
{
    IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures);
}
