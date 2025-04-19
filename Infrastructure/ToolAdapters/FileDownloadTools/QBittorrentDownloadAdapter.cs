using System.Net;
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
    protected override async Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken)
    {
        await Authenticate(cancellationToken);

        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", parameters.FileSource),
            new KeyValuePair<string, string>("savepath", downloadLocation)
        ]);
        var addTorrentResponse = await client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
        addTorrentResponse.EnsureSuccessStatusCode();

        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = "Torrent added to qBittorrent successfully"
        };
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