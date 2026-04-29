using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerVault.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = "system_prompt")]
    [Description("The system prompt that defines the agent's persona and behavior for knowledge base management")]
    public static string GetSystemPrompt()
    {
        return KnowledgeBasePrompt.AgentSystemPrompt;
    }

    [McpServerPrompt(Name = "vault_prompt")]
    [Description("Explains the Obsidian vault layout, syntax, conventions, and editing rules")]
    public static string GetVaultPrompt()
    {
        return VaultPrompt.Prompt;
    }
}