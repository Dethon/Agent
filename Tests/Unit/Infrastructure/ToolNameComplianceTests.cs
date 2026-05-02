using System.Text.RegularExpressions;
using Domain.DTOs;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// Some OpenRouter providers (notably Moonshot Kimi K2.6) enforce the OpenAI function-name
// regex strictly: ^[a-zA-Z_][a-zA-Z0-9-_]{2,63}$. Tool names with ':' are rejected, which
// surfaces as the model "not being able to call tools" — the function definition is dropped
// or the returned tool_call name is mangled, so the dispatcher can't match it.
//
// Source: https://platform.kimi.ai/docs/api/chat (ToolDefinition.function.name).
public class ToolNameComplianceTests
{
    private static readonly Regex _kimiToolNameRegex =
        new("^[a-zA-Z_][a-zA-Z0-9-_]{2,63}$", RegexOptions.Compiled);

    [Theory]
    [InlineData("mcp-vault", "vault_search")]
    [InlineData("mcp-library", "FileSearch")]
    [InlineData("mcp-websearch", "web_browse")]
    [InlineData("mcp-channel-servicebus", "send_reply")]
    public void QualifiedMcpToolName_MatchesKimiRegex(string serverName, string innerToolName)
    {
        // Mirror Infrastructure.Agents.Mcp.QualifiedMcpTool.Name construction.
        var fullName = $"mcp__{serverName}__{innerToolName}";

        _kimiToolNameRegex.IsMatch(fullName).ShouldBeTrue(
            $"Tool name '{fullName}' must comply with Kimi/OpenAI function-name regex.");
    }

    [Fact]
    public void SubAgentToolFeature_ProducedToolNames_MatchKimiRegex()
    {
        var feature = new SubAgentToolFeature(new SubAgentRegistryOptions { SubAgents = [] });

        var names = feature
            .GetTools(new FeatureConfig())
            .Select(t => t.Name)
            .ToList();

        names.ShouldNotBeEmpty();
        names.ShouldAllBe(name => _kimiToolNameRegex.IsMatch(name),
            "Every domain tool name must comply with Kimi/OpenAI function-name regex.");
    }
}
