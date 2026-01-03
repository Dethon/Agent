using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerMemory.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = MemoryPrompt.Name)]
    [Description(MemoryPrompt.Description)]
    public static string GetSystemPrompt()
    {
        return MemoryPrompt.SystemPrompt;
    }
}