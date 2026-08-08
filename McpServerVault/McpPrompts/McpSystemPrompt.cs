using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerVault.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = "vault_prompt")]
    [Description("Explains the Obsidian vault layout, syntax, conventions, and editing rules")]
    public static string GetVaultPrompt()
    {
        return VaultPrompt.Prompt;
    }
}