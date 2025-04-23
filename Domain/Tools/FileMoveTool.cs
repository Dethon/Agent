using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record FileMoveParams
{
    public required string SourceFile { get; [UsedImplicitly] init; }
    public required string DestinationDirectory { get; [UsedImplicitly] init; }
}

public class FileMoveTool(IFileSystemClient client, string libraryPath) : BaseTool, ITool
{
    public string Name => "FileMove";

    public async Task<JsonNode> Run(JsonNode? parameters, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams<FileMoveParams>(parameters);

        if (!typedParams.SourceFile.StartsWith(libraryPath) ||
            !typedParams.DestinationDirectory.StartsWith(libraryPath))
        {
            throw new ArgumentException($"""
                                         {typeof(FileMoveTool)} parameters must be absolute paths derived from the 
                                         LibraryDescription tool response. 
                                         They must start with the library path: {libraryPath}
                                         """);
        }

        await client.Move(typedParams.SourceFile, typedParams.DestinationDirectory, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved successfully",
            ["source"] = typedParams.SourceFile,
            ["destination"] = typedParams.DestinationDirectory
        };
    }

    public ToolDefinition GetToolDefinition()
    {
        return new ToolDefinition<FileMoveParams>
        {
            Name = Name,
            Description = """
                          Moves a file or directory to a destination DIRECTORY, both arguments have to be absolute paths 
                          and must be derived from the LibraryDescription tool response.
                          If the destination folder does not exist it will be created automatically.
                          When moving a file, the destination must be a directory and the source will be moved inside,
                          equivalent to 'mv' {SourceFile} {DestinationDirectory} bash command"
                          When moving a directory, the destination must be a directory and the source will be renamed as
                          the destination, equivalent to 'mv -T {SourceFile} {DestinationDirectory}' bash command.
                          """
        };
    }
}