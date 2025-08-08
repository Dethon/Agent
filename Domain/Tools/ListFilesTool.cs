using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.Tools.Config;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class ListFilesTool(IFileSystemClient client, LibraryPathConfig libraryPath) : BaseTool
{
    private const string Name = "ListFiles";

    private const string Description = """
                                       Lists all files in the specified directory. It only returns files, not 
                                       directories.
                                       The path must be absolute and derived from the ListDirectories tool.
                                       Must be used to explore the relevant directories within the library and find 
                                       the correct place and name for the downloaded files.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<CallToolResult> Run(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!path.StartsWith(libraryPath.BaseLibraryPath))
            {
                return CreateErrorResponse($"""
                                            {typeof(ListFilesTool)} parameter must be absolute paths derived from the 
                                            ListDirectories tool response. 
                                            They must start with the library path: {libraryPath}
                                            """);
            }

            var result = await client.ListFilesIn(path, cancellationToken);
            var jsonResult = JsonSerializer.SerializeToNode(result) ??
                             throw new InvalidOperationException("Failed to serialize ListFiles");
            return CreateResponse(jsonResult.ToJsonString());
        }
        catch (Exception ex)
        {
            return CreateResponse(ex);
        }
    }
}