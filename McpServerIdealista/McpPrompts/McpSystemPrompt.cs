using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerIdealista.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = IdealistaPrompt.Name)]
    [Description(IdealistaPrompt.Description)]
    public static string GetSystemPrompt()
    {
        return IdealistaPrompt.SystemPrompt;
    }
}