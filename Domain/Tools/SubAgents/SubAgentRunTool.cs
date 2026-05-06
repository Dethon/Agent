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
        [Description("When true, starts the subagent in the background and returns a handle immediately instead of waiting for completion")]
        bool run_in_background = false,
        [Description("When true, the subagent runs silently without sending progress updates to the channel")]
        bool silent = false,
        CancellationToken ct = default)
    {
        var profile = _profiles.FirstOrDefault(p =>
            p.Id.Equals(subAgentId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"Unknown subagent: '{subAgentId}'. Available: {string.Join(", ", _profiles.Select(p => p.Id))}",
                retryable: false);
        }

        if (run_in_background)
        {
            if (featureConfig.SubAgentSessions is null)
            {
                return ToolError.Create(
                    ToolError.Codes.Unavailable,
                    "Background subagent execution is not available in this context",
                    retryable: false);
            }

            try
            {
                var handle = featureConfig.SubAgentSessions.Start(profile, prompt, silent);
                return new JsonObject
                {
                    ["status"] = "started",
                    ["handle"] = handle,
                    ["subagent_id"] = profile.Id
                };
            }
            catch (InvalidOperationException ex)
            {
                return ToolError.Create(
                    ToolError.Codes.Unavailable,
                    ex.Message,
                    retryable: true);
            }
        }

        if (featureConfig.SubAgentFactory is null)
        {
            return ToolError.Create(
                ToolError.Codes.Unavailable,
                "Subagent execution is not available in this context",
                retryable: false);
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolError.Create(
                ToolError.Codes.Timeout,
                $"Subagent '{profile.Id}' exceeded its maximum execution time of {profile.MaxExecutionSeconds}s",
                retryable: true);
        }
        catch (Exception ex)
        {
            return ToolError.Create(
                ToolError.Codes.InternalError,
                ex.Message,
                retryable: true);
        }
    }
}
