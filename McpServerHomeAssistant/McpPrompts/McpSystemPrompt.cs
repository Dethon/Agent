using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerHomeAssistant.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt(HomeAssistantSetupSummary summary)
{
    [McpServerPrompt(Name = HomeAssistantPrompt.Name)]
    [Description(HomeAssistantPrompt.Description)]
    public async Task<string> GetSystemPromptAsync(CancellationToken ct)
    {
        var setup = await summary.GetAsync(ct);
        return string.IsNullOrEmpty(setup)
            ? HomeAssistantPrompt.SystemPrompt
            : HomeAssistantPrompt.SystemPrompt + "\n\n" + setup;
    }
}
