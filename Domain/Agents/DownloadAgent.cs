using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;

namespace Domain.Agents;

public class DownloadAgent(ILargeLanguageModel largeLanguageModel) : BaseAgent(largeLanguageModel), IAgent
{
    private readonly Dictionary<string, ToolDefinition> _tools = new()
    {
        { "FileDownload", new FileDownloadTool().GetToolDefinition() },
        { "FileSearch", new FileSearchTool().GetToolDefinition() }
    };

    public async Task<AgentResponse[]> Run(string userPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = Role.System,
                Content = "You are a download agent. You will help me download files from the internet."
            },
            new()
            {
                Role = Role.User,
                Content = userPrompt
            }
        };

        return await ExecuteAgentLoop(messages, _tools, cancellationToken);
    }
}