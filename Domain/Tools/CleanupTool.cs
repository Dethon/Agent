using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class CleanupTool(
    IDownloadClient downloadClient, IFileSystemClient fileSystemClient, string baseDownloadLocation)
{
    private const string Name = "Cleanup";

    private const string Description = """
                                       Removes a everything that is left over in a download directory.
                                       It can also be use to cancel a download if the user requests it.
                                       """;
    
    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(int downloadId, CancellationToken cancellationToken)
    {
        var downloadPath = $"{baseDownloadLocation}/{downloadId}";

        await downloadClient.Cleanup(downloadId, cancellationToken);
        await fileSystemClient.RemoveDirectory(downloadPath, cancellationToken);

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Download leftovers removed successfully",
            ["downloadId"] = downloadId
        }.ToJsonString();
    }
}