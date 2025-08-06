using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools;

public class ListDirectoriesTool(
    IFileSystemClient client,
    string libraryPath) : BaseTool
{
    public override string Name => "ListDirectories";

    public override string Description => """
                                          Lists all directories in the library. It only returns directories, not files.
                                          Must be used to explore the library and find the correct place into which 
                                          downloaded files should be stored.
                                          """;

    public override async Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var result = await client.ListDirectoriesIn(libraryPath, cancellationToken);
        var jsonResult = JsonSerializer.SerializeToNode(result) ?? 
                         throw new InvalidOperationException("Failed to serialize ListDirectories");
        return toolCall.ToToolMessage(jsonResult);
    }
}