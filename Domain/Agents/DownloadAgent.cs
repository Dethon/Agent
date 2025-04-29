using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

public class DownloadAgent : BaseAgent
{
    private readonly Dictionary<string, ITool> _tools;

    public DownloadAgent(
        ILargeLanguageModel largeLanguageModel,
        FileSearchTool fileSearchTool,
        FileDownloadTool fileDownloadTool,
        WaitForDownloadTool waitForDownloadTool,
        LibraryDescriptionTool libraryDescriptionTool,
        MoveTool moveTool,
        CleanupTool cleanupTool,
        List<Message> messages,
        ILogger<DownloadAgent> logger,
        int maxDepth = 10) : base(largeLanguageModel, maxDepth, logger)
    {
        _tools = new Dictionary<string, ITool>
        {
            { fileDownloadTool.Name, fileDownloadTool },
            { fileSearchTool.Name, fileSearchTool },
            { waitForDownloadTool.Name, waitForDownloadTool },
            { libraryDescriptionTool.Name, libraryDescriptionTool },
            { moveTool.Name, moveTool },
            { cleanupTool.Name, cleanupTool }
        };
        Messages = messages;
        if (Messages.Count == 0)
        {
            Messages.Add(new Message
            {
                Role = Role.System,
                Content = DownloadSystemPrompt.Prompt
            });
        }
    }

    public override IAsyncEnumerable<AgentResponse> Run(
        string userPrompt, CancellationToken cancellationToken = default)
    {
        Messages.Add(new Message
        {
            Role = Role.User,
            Content = userPrompt
        });
        return ExecuteAgentLoop(_tools, 0.5f, cancellationToken);
    }
}