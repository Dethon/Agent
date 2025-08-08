using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.Tools.Config;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class ListDirectoriesTool(IFileSystemClient client, LibraryPathConfig libraryPath) : BaseTool
{
    private const string Name = "ListDirectories";

    private const string Description = """
                                       Lists all directories in the library. It only returns directories, not files.
                                       Must be used to explore the library and find the correct place into which 
                                       downloaded files should be stored.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<CallToolResult> Run(CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.ListDirectoriesIn(libraryPath.BaseLibraryPath, cancellationToken);
            var jsonResult = JsonSerializer.SerializeToNode(result);

            return jsonResult is null
                ? CreateErrorResponse("Failed to serialize ListDirectories")
                : CreateResponse(jsonResult);
        }
        catch (Exception ex)
        {
            return CreateResponse(ex);
        }
    }
}