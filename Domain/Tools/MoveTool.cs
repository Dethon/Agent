using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.Tools;

[UsedImplicitly]
public record FileMoveParams
{
    public required string SourcePath { get; [UsedImplicitly] init; }
    public required string DestinationPath { get; [UsedImplicitly] init; }
}

public class MoveTool(
    IFileSystemClient client,
    string libraryPath) : BaseTool<FileMoveParams>
{
    public override string Name => "Move";

    public override string Description => """
                                          Moves and/or renames a file or directory. Both arguments have to be absolute 
                                          paths and must be derived from the LibraryDescription tool response.
                                          Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                          The destination path MUST NOT exist, otherwise an exception will be thrown.
                                          All necessary parent directories will be created automatically.
                                          """;

    public override async Task<ToolMessage> Run(ToolCall toolCall, CancellationToken cancellationToken = default)
    {
        var typedParams = ParseParams(toolCall.Parameters);

        if (!typedParams.SourcePath.StartsWith(libraryPath) ||
            !typedParams.DestinationPath.StartsWith(libraryPath))
        {
            throw new ArgumentException($"""
                                         {typeof(MoveTool)} parameters must be absolute paths derived from the 
                                         LibraryDescription tool response. 
                                         They must start with the library path: {libraryPath}
                                         """);
        }

        await client.Move(typedParams.SourcePath, typedParams.DestinationPath, cancellationToken);
        return toolCall.ToToolMessage(new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved successfully",
            ["source"] = typedParams.SourcePath,
            ["destination"] = typedParams.DestinationPath
        });
    }
}