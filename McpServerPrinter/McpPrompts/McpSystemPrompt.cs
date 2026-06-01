using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = PrintingPrompt.Name)]
    [Description(PrintingPrompt.Description)]
    public string GetPrintingPrompt() => PrintingPrompt.Prompt;
}