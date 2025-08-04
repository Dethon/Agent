using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Tools;

public record ListFilesParams
{
    public required string Path { get; [UsedImplicitly] init; }
}

public class ListFilesTool(
    IFileSystemClient client,
    string libraryPath) : BaseTool<ListFilesTool, ListFilesParams>, IToolWithMetadata
{
    public static string Name => "ListFiles";

    public static string Description => """
                                        Lists all files in the specified directory. It only returns files, not 
                                        directories.
                                        The path must be absolute and derived from the ListDirectories tool.
                                        Must be used to explore the relevant directories within the library and find 
                                        the correct place and name for the downloaded files.
                                        """;

    public override async Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(toolCall.Parameters);
        if (!typedParams.Path.StartsWith(libraryPath))
        {
            throw new ArgumentException($"""
                                         {typeof(ListFilesTool)} parameter must be absolute paths derived from the 
                                         ListDirectories tool response. 
                                         They must start with the library path: {libraryPath}
                                         """);
        }

        var result = await client.ListFilesIn(typedParams.Path, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result) ?? 
                         throw new InvalidOperationException("Failed to serialize ListFiles");
        return toolCall.ToToolMessage(jsonResult); 
    }
}