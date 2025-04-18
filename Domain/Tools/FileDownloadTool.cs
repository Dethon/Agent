using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public record FileDownloadParams
{
    public required string FileSource { get; init; }
}

public class FileDownloadTool : ITool
{
    public string Name => "FileDownload";

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileDownloadParams>
        {
            Name = Name,
            Description = "Download a file from the internet using a file source",
            Parameters = new FileDownloadParams
            {
                FileSource = string.Empty
            }
        };
    }
}