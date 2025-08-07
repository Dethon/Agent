using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace Domain.Resources;

[McpServerResourceType]
public class DownloadSummaryResource(IDownloadClient downloadClient)
{
    [McpServerResource(
        UriTemplate = "download://{id}/", 
        Name = "Download Summary", 
        MimeType = "text/plain")]
    [Description("A download summary resource")]
    public async Task<string> Get(int id, CancellationToken cancellationToken)
    {
        var downloadStatus = await downloadClient.GetDownloadItem(id, 3, 500, cancellationToken);
        return downloadStatus == null
            ? $"The download with ID {id} could not be found. It was probably removed externally"
            : $"The status of the download with ID {id} is: {JsonSerializer.Serialize(downloadStatus)}";
    }
}