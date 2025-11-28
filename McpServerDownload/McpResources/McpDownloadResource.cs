using System.ComponentModel;
using Domain.Contracts;
using Domain.Resources;
using ModelContextProtocol.Server;

namespace McpServerDownload.McpResources;

[McpServerResourceType]
public class McpDownloadResource(IDownloadClient downloadClient) : DownloadResource(downloadClient)
{
    [McpServerResource(
        UriTemplate = "download://{id}/",
        Name = "Download Summary",
        MimeType = "text/plain")]
    [Description("A download summary resource")]
    public async Task<string> McpGet(int id, CancellationToken cancellationToken)
    {
        return await Get(id, cancellationToken);
    }
}