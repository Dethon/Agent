using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerScheduling.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt(ScheduleSetupSummary summary)
{
    [McpServerPrompt(Name = SchedulingPrompt.Name)]
    [Description(SchedulingPrompt.Description)]
    public string GetSchedulingPrompt()
    {
        var setup = summary.Get();
        return string.IsNullOrEmpty(setup)
            ? SchedulingPrompt.Prompt
            : SchedulingPrompt.Prompt + "\n\n" + setup;
    }
}