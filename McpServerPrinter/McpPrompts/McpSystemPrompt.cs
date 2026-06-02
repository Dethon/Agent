using System.ComponentModel;
using Domain.Prompts;
using McpServerPrinter.Settings;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt(PrinterSettings settings)
{
    [McpServerPrompt(Name = PrintingPrompt.Name)]
    [Description(PrintingPrompt.Description)]
    public string GetPrintingPrompt() => PrintingPrompt.Build(settings.SupportedFormats);
}