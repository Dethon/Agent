using Domain.Contracts;

namespace Domain.Resources;

public class DownloadResource(IDownloadClient downloadClient)
{
    public async Task<string> Get(int downloadId, CancellationToken cancellationToken)
    {
        var downloadStatus = await downloadClient.GetDownloadItem(downloadId, cancellationToken);
        return downloadStatus is null
            ? $"The download with id {downloadId} is missing, it probably got removed externally."
            : $"The status of the download with id {downloadId} is {downloadStatus.State}";
    }
}