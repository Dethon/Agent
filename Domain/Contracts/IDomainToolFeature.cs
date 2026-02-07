using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolFeature
{
    string FeatureName { get; }
    IEnumerable<AIFunction> GetTools();
}
