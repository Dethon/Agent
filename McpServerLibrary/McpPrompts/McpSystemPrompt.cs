using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    private const string Name = "system_prompt";

    [McpServerPrompt(Name = Name)]
    [Description("The system prompt that defines the agent's persona and behavior")]
    public static string GetSystemPrompt()
    {
        return DownloaderPrompt.AgentSystemPrompt;
    }
}