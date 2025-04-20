using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Infrastructure.Clients;

public class QBittorrentDownloadClient(
    HttpClient client,
    CookieContainer cookieContainer,
    string user,
    string password)
    : IDownloadClient
{
    public async Task Download(string link, string savePath, string id, CancellationToken cancellationToken = default)
    {
        await Authenticate(cancellationToken);
        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", link),
            new KeyValuePair<string, string>("savepath", savePath),
            new KeyValuePair<string, string>("rename", $"{id}")
        ]);
        var addTorrentResponse = await client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
        addTorrentResponse.EnsureSuccessStatusCode();
        await Task.Delay(5000, cancellationToken); // Wait to make sure the torrent got added
        if (await GetDownloadingTorrent(id, cancellationToken) is null)
        {
            throw new InvalidOperationException("Torrent cannot be added. Try another link. Search again if necessary");
        }
    }

    public async Task<bool> IsDownloadComplete(string id, CancellationToken cancellationToken = default)
    {
        var torrent = await GetDownloadingTorrent(id, cancellationToken);
        var progress = torrent?["progress"]?.GetValue<double>() ?? 0;
        var state = torrent?["state"]?.GetValue<string>() ?? string.Empty;
        var isCompleted = torrent is null ||
                          progress >= 1.0 ||
                          state.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                          state.Equals("finished", StringComparison.OrdinalIgnoreCase);

        return isCompleted;
    }

    private async Task<JsonNode?> GetDownloadingTorrent(string id, CancellationToken cancellationToken)
    {
        await Authenticate(cancellationToken);
        var torrents = await client.GetFromJsonAsync<JsonNode[]>("torrents/info", cancellationToken);
        return torrents?.SingleOrDefault(x =>
        {
            var name = x["name"]?.GetValue<string>() ?? string.Empty;
            return name.Equals(id, StringComparison.OrdinalIgnoreCase);
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