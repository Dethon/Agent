using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentRunTool(
    SubAgentRegistryOptions registryOptions,
    FeatureConfig featureConfig)
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

        if (featureConfig.SubAgentFactory is null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["error"] = "Subagent execution is not available in this context"
            };
        }

        try
        {
            await using var agent = featureConfig.SubAgentFactory(profile);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(profile.MaxExecutionSeconds));

            var userMessage = new ChatMessage(ChatRole.User, prompt);
            userMessage.SetSenderId(featureConfig.UserId);
            var response = await agent.RunAsync(
                [userMessage], cancellationToken: timeoutCts.Token);

            return new JsonObject
            {
                ["status"] = "completed",
                ["result"] = response.Text
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