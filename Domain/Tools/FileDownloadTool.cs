using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public record FileDownloadParams
{
    public required int SearchResultId { get; init; }
}

public abstract class FileDownloadTool : ITool
{
    public string Name => "FileDownload";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = parameters?.Deserialize<FileDownloadParams>();
        if (typedParams is null)
        {
            throw new ArgumentNullException(
                nameof(parameters), $"{typeof(FileDownloadTool)} cannot have null parameters");
        }

        return await Resolve(typedParams, cancellationToken);
    }

    protected abstract Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken);
    public abstract Task<bool> IsDownloadComplete(CancellationToken cancellationToken);

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileDownloadParams>
        {
            Name = Name,
            Description = """
                          Download a file from the internet using a file id that can be obtained from the FileSearch 
                          tool. The SearchResultId parameter is the id EXACTLY as it appears in the response of the
                          FileSearch tool
                          """
        };
    }
}