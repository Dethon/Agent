using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolFeature
{
    string FeatureName { get; }
    string? Prompt => null;
    IEnumerable<AIFunction> GetTools(FeatureConfig config);
}