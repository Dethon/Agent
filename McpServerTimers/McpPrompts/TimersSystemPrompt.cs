using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerTimers.McpPrompts;

[McpServerPromptType]
public class TimersSystemPrompt
{
    [McpServerPrompt(Name = TimerPrompt.Name)]
    [Description(TimerPrompt.Description)]
    public string GetTimerPrompt() => TimerPrompt.Prompt;
}