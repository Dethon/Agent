using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Tools;
using Domain.Tools.Attachments;

namespace Infrastructure.ToolAdapters.FileDownloadTools;

public class QBittorrentDownloadAdapter(
    HttpClient client,
    CookieContainer cookieContainer,
    string user,
    string password,
    string downloadLocation,
    SearchHistory searchHistory)
    : FileDownloadTool
{
    private string? _torrentName;

    protected override async Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken)
    {
        if (!await IsDownloadComplete(cancellationToken))
        {
            throw new InvalidOperationException("Download in progress");
        }

        await Authenticate(cancellationToken);
        var link = searchHistory.History[parameters.SearchResultId].Link;
        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", link),
            new KeyValuePair<string, string>("savepath", downloadLocation),
            new KeyValuePair<string, string>("rename", $"{parameters.SearchResultId}")
        ]);
        var addTorrentResponse = await client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
        addTorrentResponse.EnsureSuccessStatusCode();
        _torrentName = $"{parameters.SearchResultId}";

        await Task.Delay(5000, cancellationToken); // Wait to make sure the torrent got added
        if (await GetDownloadingTorrent(cancellationToken) is not null)
        {
            return new JsonObject
            {
                ["status"] = "success",
                ["message"] = "Torrent added to qBittorrent successfully",
                ["downloadId"] = parameters.SearchResultId
            };
        }

        _torrentName = null;
        return new JsonObject
        {
            ["status"] = "Error",
            ["message"] = "Torrent cannot be added. try another link. Search again if necessary"
        };
    }

    public override async Task<bool> IsDownloadComplete(CancellationToken cancellationToken)
    {
        if (_torrentName == null)
        {
            return true;
        }

        var torrent = await GetDownloadingTorrent(cancellationToken);
        var progress = torrent?["progress"]?.GetValue<double>() ?? 0;
        var state = torrent?["state"]?.GetValue<string>() ?? string.Empty;
        var isCompleted = torrent is null ||
                          progress >= 1.0 ||
                          state.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                          state.Equals("finished", StringComparison.OrdinalIgnoreCase);

        if (isCompleted)
        {
            _torrentName = null;
        }

        return isCompleted;
    }

    private async Task<JsonNode?> GetDownloadingTorrent(CancellationToken cancellationToken)
    {
        await Authenticate(cancellationToken);
        var torrents = await client.GetFromJsonAsync<JsonNode[]>("torrents/info", cancellationToken);
        return torrents?.SingleOrDefault(x =>
        {
            var name = x["name"]?.GetValue<string>() ?? string.Empty;
            return name.Equals(_torrentName, StringComparison.OrdinalIgnoreCase);
        });
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