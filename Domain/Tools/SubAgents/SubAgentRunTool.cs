using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.SubAgents;

public class SubAgentRunTool(
    ISubAgentRunner runner,
    ISubAgentContextAccessor contextAccessor,
    SubAgentRegistryOptions registryOptions)
{
    public const string Name = "run_subagent";

    private readonly SubAgentDefinition[] _profiles = registryOptions.SubAgents;

    public string Description
    {
        get
        {
            var profileList = string.Join("\n",
                _profiles.Select(p => $"- \"{p.Id}\": {p.Description ?? p.Name}"));
            return $"""
                    Runs a task on a subagent with a fresh context and returns the result.
                    Available subagents:
                    {profileList}
                    """;
        }
    }

    public async Task<JsonNode> RunAsync(
        [Description("ID of the subagent profile to use")]
        string subAgentId,
        [Description("The task/prompt to send to the subagent")]
        string prompt,
        [Description("Your own agent name (from your system prompt)")]
        string agentName,
        CancellationToken ct = default)
    {
        var profile = _profiles.FirstOrDefault(p =>
            p.Id.Equals(subAgentId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = $"Unknown subagent: '{subAgentId}'. Available: {string.Join(", ", _profiles.Select(p => p.Id))}"
            };
        }

        var context = contextAccessor.GetContext(agentName);
        if (context is null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = "Subagent context not available for this agent"
            };
        }

        try
        {
            var result = await runner.RunAsync(profile, prompt, context, ct);
            return new JsonObject
            {
                ["status"] = "completed",
                ["result"] = result
            };
        }
        catch (Exception ex)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = ex.Message
            };
        }
    }
}
