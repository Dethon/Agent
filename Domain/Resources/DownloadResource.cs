using System.ComponentModel;
using Domain.Contracts;
using ModelContextProtocol.Server;

namespace Domain.Resources;

[McpServerResourceType]
public class DownloadResource(IDownloadClient downloadClient)
{
    [McpServerResource(
        UriTemplate = "download://{id}/", 
        Name = "Download Summary", 
        MimeType = "text/plain")]
    [Description("A download summary resource")]
    public async Task<string> Get(int id, CancellationToken cancellationToken)
    {
        var downloadStatus = await downloadClient.GetDownloadItem(id, 3, 500, cancellationToken);
        return downloadStatus is null
            ? $"The download with id {id} is missing, it probably got removed externally."
            : $"The status of the download with id {id} is {downloadStatus.State}";
    }
}