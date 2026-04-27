using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    private const string Name = "sandbox_prompt";

    [McpServerPrompt(Name = Name)]
    [Description("Explains the sandbox filesystem layout, capabilities, and limits")]
    public static string GetSandboxPrompt()
    {
        return SandboxPrompt.Prompt;
    }
}
