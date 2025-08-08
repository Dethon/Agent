using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class CleanupDownloadDirectoryTool(IFileSystemClient fileSystemClient, DownloadPathConfig downloadPath)
{
    private const string Name = "CleanupDownloadDirectory";

    private const string Description = """
                                       Removes a everything that is left over in a download directory.
                                       It can also be use to cancel a download if the user requests it.
                                       """;
    
    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(int downloadId, CancellationToken cancellationToken)
    {
        var path = $"{downloadPath.BaseDownloadPath}/{downloadId}";
        await fileSystemClient.RemoveDirectory(path, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download leftover files removed successfully",
            ["downloadId"] = downloadId
        }.ToJsonString();
    }
}