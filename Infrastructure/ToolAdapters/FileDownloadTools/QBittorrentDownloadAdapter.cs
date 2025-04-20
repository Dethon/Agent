using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.FileDownloadTools;

public class QBittorrentDownloadAdapter(
    HttpClient client,
    CookieContainer cookieContainer,
    string user,
    string password,
    string downloadLocation)
    : FileDownloadTool
{
    private Guid? _torrentName;

    protected override async Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken)
    {
        if (!await IsDownloadComplete(cancellationToken))
        {
            throw new InvalidOperationException("Download in progress");
        }

        await Authenticate(cancellationToken);
        var downloadId = Guid.NewGuid();
        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", parameters.FileSource),
            new KeyValuePair<string, string>("savepath", downloadLocation),
            new KeyValuePair<string, string>("rename", downloadId.ToString())
        ]);
        var addTorrentResponse = await client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
        addTorrentResponse.EnsureSuccessStatusCode();
        _torrentName = downloadId;

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Torrent added to qBittorrent successfully",
            ["downloadId"] = downloadId.ToString()
        };
    }

    public override async Task<bool> IsDownloadComplete(CancellationToken cancellationToken)
    {
        if (_torrentName == null)
        {
            return true;
        }

        await Authenticate(cancellationToken);
        var torrents = await client.GetFromJsonAsync<JsonNode[]>("torrents/info", cancellationToken);

        var isDownloadComplete = torrents?.Any(x =>
        {
            var name = x["name"]?.GetValue<string>() ?? string.Empty;
            var progress = x["progress"]?.GetValue<double>() ?? 0;
            var state = x["state"]?.GetValue<string>() ?? string.Empty;
            var isCompleted = progress >= 1.0 ||
                              state.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                              state.Equals("finished", StringComparison.OrdinalIgnoreCase);

            return name.Equals(_torrentName.ToString(), StringComparison.OrdinalIgnoreCase) && isCompleted;
        }) ?? true;

        if (isDownloadComplete)
        {
            _torrentName = null;
        }

        return isDownloadComplete;
    }

    private async Task Authenticate(CancellationToken cancellationToken)
    {
        if (IsAuthenticated())
        {
            return;
        }

        var loginContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("username", user),
            new KeyValuePair<string, string>("password", password)
        ]);
        var loginResponse = await client.PostAsync("auth/login", loginContent, cancellationToken);
        loginResponse.EnsureSuccessStatusCode();
    }

    private bool IsAuthenticated()
    {
        var baseAddress = client.BaseAddress;
        if (baseAddress is null)
        {
            throw new InvalidOperationException("Base address is not set for the HttpClient.");
        }

        var cookie = cookieContainer.GetCookies(baseAddress)["SID"];
        return cookie is { Expired: false };
    }
}