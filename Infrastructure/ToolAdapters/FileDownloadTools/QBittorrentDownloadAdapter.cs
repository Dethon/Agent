using System.Net;
using System.Text.Json.Nodes;
using Domain.Tools;

namespace Infrastructure.ToolAdapters.FileDownloadTools;

public class QBittorrentDownloadAdapter : FileDownloadTool
{
    private readonly CookieContainer _cookieContainer;
    private readonly HttpClient _client;
    private readonly string _user;
    private readonly string _password;

    public QBittorrentDownloadAdapter(HttpClient client, string user, string password, CookieContainer cookieContainer)
    {
        _client = client;
        _user = user;
        _password = password;
        _cookieContainer = cookieContainer;
    }

    protected override async Task<JsonNode> Resolve(FileDownloadParams parameters, CancellationToken cancellationToken)
    {
        await Authenticate(cancellationToken);
        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", parameters.FileSource)
        ]);

        var addTorrentResponse = await _client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
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
            new KeyValuePair<string, string>("username", _user),
            new KeyValuePair<string, string>("password", _password)
        ]);
        var loginResponse = await _client.PostAsync("auth/login", loginContent, cancellationToken);
        loginResponse.EnsureSuccessStatusCode();
    }

    private bool IsAuthenticated()
    {
        var baseAddress = _client.BaseAddress;
        if (baseAddress is null)
        {
            throw new InvalidOperationException("Base address is not set for the HttpClient.");
        }

        var cookie = _cookieContainer.GetCookies(baseAddress)["SID"];
        return cookie is { Expired: false };
    }
}