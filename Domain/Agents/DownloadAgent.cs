using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.Attachments;

namespace Domain.Agents;

public class DownloadAgent(
    ILargeLanguageModel largeLanguageModel,
    FileSearchTool fileSearchTool,
    FileDownloadTool fileDownloadTool,
    LibraryDescriptionTool libraryDescriptionTool,
    FileMoveTool fileMoveTool,
    DownloadMonitor downloadMonitor) : BaseAgent(largeLanguageModel), IAgent
{
    private readonly Dictionary<string, ITool> _tools = new()
    {
        { fileDownloadTool.Name, fileDownloadTool },
        { fileSearchTool.Name, fileSearchTool },
        { libraryDescriptionTool.Name, libraryDescriptionTool },
        { fileMoveTool.Name, fileMoveTool }
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
                          To generate search strings make them concise and generic, for example title and category is a
                          good search string. 
                          don't include too much specific information in the search string as that produces worse 
                          results, that information should only be used to choose what to download from the list.
                          You should always try to perform several searches with slightly different search strings to
                          have a better chance of finding relevant results, changing the separators between words yields 
                          good results.
                          You can search for multiple alternative search strings at the same time.
                          If no relevant results are found or if they are subpar in terms of quality or number of 
                          seeders you must try with slightly different search strings, for example in video or movies 
                          anything lower than 1080p is bad quality.
                          The search string will be used to search across a set of torrent trackers, so you can try to 
                          optimize them for this kind of search.
                          Prioritize high-quality content that is NOT HDR, bigger files with better bitrate are usually 
                          preferred over lighter alternatives.
                          You are allowed to start the download of just one file, the one you find most appropriate. DO
                          NOT ask the user to confirm the choice. 
                          Once the download finishes you will be asked to organize it within the library, when you 
                          receive that command you will be able to explore the library structure and move files 
                          accordingly.
                          """
            },
            new()
            {
                Role = Role.User,
                Content = userPrompt
            }
        };

        messages = await ExecuteAgentLoop(messages, _tools, cancellationToken);

        while (await downloadMonitor.AreDownloadsPending(cancellationToken))
        {
            await Task.Delay(1000, cancellationToken);
            foreach (var id in await downloadMonitor.PopCompletedDownloads(cancellationToken))
            {
                messages.Add(new Message
                {
                    Role = Role.User,
                    Content = $"""
                               The download with id {id} just finished. Organize the files that were downloaded according to the current 
                               library structure. Hint: Use the LibraryDescription and FileMove tools.
                               If there is no appropriate folder for the category you should create it.
                               """
                });
                await ExecuteAgentLoop(messages, _tools, cancellationToken);
            }
        }

        return messages;
    }
}