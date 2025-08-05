using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Exceptions;
using Domain.Tools.Attachments;
using JetBrains.Annotations;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol.Server;

namespace Domain.Tools;

[McpServerToolType]
public class GetDownloadStatusTool(IDownloadClient client, IMemoryCache cache)
{
    private const string Name = "GetDownloadStatus";

    private const string Description = """
                                       Returns the status of download referenced by DownloadId.
                                       Progress is a percentage from 0 to 1, with 1 meaning 100%.
                                       DownSpeed and UpSpeed are in megabytes per second.
                                       Size is the total size of the download in megabytes.
                                       Eta is the estimated time until the completion of the download on minutes.
                                       """;

    [McpServerTool(Name = Name), Description(Description)]
    public async Task<string> Run(int downloadId, CancellationToken cancellationToken)
    {
        var downloadItem = await client.GetDownloadItem(downloadId, 3, 500, cancellationToken);
        if (downloadItem == null)
        {
            return "The download is missing, it probably got removed externally";
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = JsonSerializer.Serialize(downloadItem with
            {
                Title = cache.Get<SearchResult>(downloadItem.Id)?.Title ?? "Missing Title",
            })
        }.ToJsonString();
    }
}