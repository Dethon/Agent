using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public record FileDownloadParams
{
    public required string FileSource { get; init; }
}

public abstract class FileDownloadTool : ITool
{
    public string Name => "FileDownload";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = parameters?.Deserialize<FileDownloadParams>();
        if (typedParams is null)
            throw new ArgumentNullException(
                nameof(parameters), $"{typeof(FileDownloadTool)} cannot have null parameters");

        return await Resolve(typedParams, cancellationToken);
    }

    protected abstract Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken);

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