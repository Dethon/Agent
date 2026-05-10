using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = HomeAssistantPrompt.Name)]
    [Description(HomeAssistantPrompt.Description)]
    public static string GetSystemPrompt() => HomeAssistantPrompt.SystemPrompt;
}
