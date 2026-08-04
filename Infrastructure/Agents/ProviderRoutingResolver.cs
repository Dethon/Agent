using Domain.DTOs;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Agents;

internal static class ProviderRoutingResolver
{
    // Wholesale replacement, not a per-field merge: an agent that declares routing owns the
    // whole object, so it can never inherit an `ignore` list invisible at its own config site.
    // Advisories run on the resolved value and fire per call with no dedupe, which is per
    // conversation activation for an agent and per spawn for a subagent.
    public static ProviderRouting? Resolve(
        ProviderRouting? declared,
        ProviderRouting? globalDefault,
        string model,
        string advisoryIdentity,
        ILogger? logger)
    {
        var effective = declared ?? globalDefault;

        foreach (var advisory in ProviderRoutingAdvisories.For(model, effective))
        {
            logger?.LogWarning("Agent '{AgentId}': {Advisory}", advisoryIdentity, advisory);
        }

        return effective;
    }
}