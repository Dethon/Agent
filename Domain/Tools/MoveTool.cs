using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class MoveTool(IFileSystemClient client, LibraryPathConfig libraryPath) : BaseTool
{
    private const string Name = "Move";

    private const string Description = """
                                       Moves and/or renames a file or directory. Both arguments have to be absolute 
                                       paths and must be derived from the LibraryDescription tool response.
                                       Equivalent to 'mv -T {SourcePath} {DestinationPath}' bash command.
                                       The destination path MUST NOT exist, otherwise an exception will be thrown.
                                       All necessary parent directories will be created automatically.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<CallToolResult> Run(string sourcePath, string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!sourcePath.StartsWith(libraryPath.BaseLibraryPath) ||
                !destinationPath.StartsWith(libraryPath.BaseLibraryPath))
            {
                return CreateErrorResponse($"""
                                            {typeof(MoveTool)} parameters must be absolute paths derived from the 
                                            LibraryDescription tool response. 
                                            They must start with the library path: {libraryPath}
                                            """);
            }

            await client.Move(sourcePath, destinationPath, cancellationToken);
            return CreateResponse(new JsonObject
            {
                ["status"] = "success",
                ["message"] = "File moved successfully",
                ["source"] = sourcePath,
                ["destination"] = destinationPath
            });
        }
        catch (Exception ex)
        {
            return CreateResponse(ex);
        }
    }
}