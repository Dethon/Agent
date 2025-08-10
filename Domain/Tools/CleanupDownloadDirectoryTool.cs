using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Config;

namespace Domain.Tools;

public class CleanupDownloadDirectoryTool(IFileSystemClient fileSystemClient, DownloadPathConfig downloadPath)
{
    protected const string Name = "CleanupDownloadDirectory";

    protected const string Description = """
                                         Removes a everything that is left over in a download directory.
                                         It can also be use to cancel a download if the user requests it.
                                         """;

    protected async Task<JsonNode> Run(int downloadId, CancellationToken cancellationToken)
    {
        var path = $"{downloadPath.BaseDownloadPath}/{downloadId}";
        await fileSystemClient.RemoveDirectory(path, cancellationToken);
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download leftover files removed successfully",
            ["downloadId"] = downloadId
        };
    }
}