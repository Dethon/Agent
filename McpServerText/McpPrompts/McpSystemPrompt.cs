using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerText.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    private const string Name = "system_prompt";

    [McpServerPrompt(Name = Name)]
    [Description("The system prompt that defines the agent's persona and behavior for knowledge base management")]
    public static string GetSystemPrompt()
    {
        return KnowledgeBasePrompt.AgentSystemPrompt;
    }
}