﻿using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools;

public class MoveTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "Move";

    protected const string Description = """
                                         Moves and/or renames a file or directory. Both arguments have to be absolute 
                                         paths and must be derived from the LibraryDescription tool response.
                                         Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                         The destination path MUST NOT exist, otherwise an exception will be thrown.
                                         All necessary parent directories will be created automatically.
                                         """;

    public async Task<JsonNode> Run(string sourcePath, string destinationPath, CancellationToken ct)
    {
        if (!sourcePath.StartsWith(libraryPath.BaseLibraryPath) ||
            !destinationPath.StartsWith(libraryPath.BaseLibraryPath))
        {
            throw new InvalidOperationException($"""
                                                 {typeof(MoveTool)} parameters must be absolute paths derived from
                                                 the ListDirectories tool response. 
                                                 They must start with the library path: {libraryPath}
                                                 """);
        }

        await client.Move(sourcePath, destinationPath, ct);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "File moved successfully",
            ["source"] = sourcePath,
            ["destination"] = destinationPath
        };
    }
}