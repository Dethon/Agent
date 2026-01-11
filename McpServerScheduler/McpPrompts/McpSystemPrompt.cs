using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerScheduler.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = SchedulerPrompt.Name)]
    [Description(SchedulerPrompt.Description)]
    public static string GetSystemPrompt()
    {
        return SchedulerPrompt.SystemPrompt;
    }
}
