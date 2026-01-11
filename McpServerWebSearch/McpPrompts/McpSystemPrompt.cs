using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    private const string Name = "system_prompt";

    [McpServerPrompt(Name = Name)]
    [Description(
        "System prompt for web research and browsing agent with search, navigation, and interaction capabilities")]
    public static string GetSystemPrompt()
    {
        return WebBrowsingPrompt.AgentSystemPrompt;
    }
}