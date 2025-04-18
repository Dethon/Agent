using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;

namespace Domain.Agents;

public class DownloadAgent(
    ILargeLanguageModel largeLanguageModel,
    FileSearchTool fileSearchTool,
    FileDownloadTool fileDownloadTool) : BaseAgent(largeLanguageModel), IAgent
{
    private readonly Dictionary<string, ITool> _tools = new()
    {
        { fileDownloadTool.Name, fileDownloadTool },
        { fileSearchTool.Name, fileSearchTool }
    };

    public async Task<AgentResponse[]> Run(string userPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = Role.System,
                Content = """
                          You are a download agent. You will help the user download files from the internet.
                          Prioritize high-quality content that matches with the user intent.
                          To generate search strings make them concise and highly specific, title and category is 
                          usually a good search string. 
                          Avoid including language or specific resolutions in the search string, that information 
                          should only be used to chose what to download from the list.
                          If no relevant results are found you should try with slightly different search strings.
                          The search string will be used to search across a set of torrent trackers, so you can 
                          try to optimize them for this kind of search.
                          """
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