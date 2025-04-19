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

    public async Task<List<Message>> Run(string userPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = Role.System,
                Content = """
                          You are a download agent. You will help the user download files from the internet.
                          To generate search strings make them concise, for example title and category is a good 
                          search string. 
                          don't include too much specific information in the search string as that produces worse 
                          results, that information should only be used to choose what to download from the list.
                          If no relevant results are found or if they are subpar in terms of quality or number of 
                          seeders you must try with slightly different search strings, for example in video or movies 
                          anything lower than 1080p is bad quality.
                          Prioritize high-quality content that matches with the user intent over download speed, bigger
                          files with better bitrate are usually preferred over lighter alternatives.
                          You can try to search for multiple alternative search strings at the same time.
                          The search string will be used to search across a set of torrent trackers, so you can try to 
                          optimize them for this kind of search.
                          For the download, once you have all the information you need you will make a decision without 
                          asking the user.
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