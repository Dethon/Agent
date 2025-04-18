using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public record FileSearchParams
{
    public required string SearchString { get; init; }
}

public class FileSearchTool : ITool
{
    public string Name => "FileSearch";

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileSearchParams>
        {
            Name = Name,
            Description = "Search for file in the internet using a search string",
            Parameters = new FileSearchParams
            {
                SearchString = string.Empty
            }
        };
    }
}