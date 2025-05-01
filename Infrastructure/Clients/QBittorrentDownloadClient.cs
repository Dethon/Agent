using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients;

public class QBittorrentDownloadClient(
    HttpClient client,
    CookieContainer cookieContainer,
    string user,
    string password)
    : IDownloadClient
{
    public async Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default)
    {
        await Authenticate(cancellationToken);
        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", link),
            new KeyValuePair<string, string>("savepath", savePath),
            new KeyValuePair<string, string>("rename", $"{id}")
        ]);
        var addTorrentResponse = await client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
        addTorrentResponse.EnsureSuccessStatusCode();
        await Task.Delay(10000, cancellationToken); // Wait to make sure the torrent got added
        if (await GetSingleTorrent($"{id}", cancellationToken) is null)
        {
            throw new InvalidOperationException("Torrent cannot be added. Try another link. Search again if necessary");
        }
    }

    public async Task Cleanup(int id, CancellationToken cancellationToken = default)
    {
        await Authenticate(cancellationToken);

        var torrent = await GetSingleTorrent($"{id}", cancellationToken);
        if (torrent is null)
        {
            return;
        }

        var hash = torrent["hash"]?.GetValue<string>();
        if (string.IsNullOrEmpty(hash))
        {
            throw new InvalidOperationException("Cannot cleanup torrent: unable to get hash");
        }

        var deleteTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", hash),
            new KeyValuePair<string, string>("deleteFiles", "true")
        ]);

        var deleteTorrentResponse = await client
            .PostAsync("torrents/delete", deleteTorrentContent, cancellationToken);
        deleteTorrentResponse.EnsureSuccessStatusCode();
    }

    public async Task<IEnumerable<DownloadItem>> RefreshDownloadItems(
        IEnumerable<DownloadItem> items, CancellationToken cancellationToken = default)
    {
        await Authenticate(cancellationToken);
        var torrents = (await GetAllTorrents(cancellationToken))
            .ToLookup(x => x["name"]?.GetValue<string>() ?? string.Empty, x => x)
            .ToDictionary(x => x.Key, x => x.First());

        return items.Select(x =>
            {
                var torrent = torrents.GetValueOrDefault($"{x.Id}");
                if (torrent is null)
                {
                    return null;
                }

                return x with
                {
                    Status = GetDownloadStatus(torrent)
                };
            })
            .Where(x => x is not null)
            .Cast<DownloadItem>();
    }

    public async Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default)
    {
        var torrent = await GetSingleTorrent($"{id}", cancellationToken);
        return new DownloadItem
        {
            Id = id,
            Title = torrent?["name"]?.GetValue<string>() ?? string.Empty,
            Size = torrent?["total_size"]?.GetValue<long>() ?? 0,
            Status = GetDownloadStatus(torrent),
            Seeders = torrent?["num_seeds"]?.GetValue<int>() ?? 0,
            Peers = torrent?["num_leechs"]?.GetValue<int>() ?? 0,
            SavePath = torrent?["save_path"]?.GetValue<string>() ?? string.Empty,
            Link = torrent?["magnet_uri"]?.GetValue<string>() ?? string.Empty,
        };
    }

    public async Task<bool> IsDownloadComplete(int id, CancellationToken cancellationToken = default)
    {
        var torrent = await GetSingleTorrent($"{id}", cancellationToken);
        return GetDownloadStatus(torrent) == DownloadStatus.Completed;
    }

    private async Task<JsonNode?> GetSingleTorrent(string id, CancellationToken cancellationToken)
    {
        var torrents = await GetAllTorrents(cancellationToken);
        return torrents.SingleOrDefault(x =>
        {
            var name = x["name"]?.GetValue<string>() ?? string.Empty;
            return name.Equals(id, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task<JsonNode[]> GetAllTorrents(CancellationToken cancellationToken)
    {
        var torrents = await client.GetFromJsonAsync<JsonNode[]>("torrents/info", cancellationToken);
        return torrents ?? [];
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

    private static DownloadStatus GetDownloadStatus(JsonNode? torrent)
    {
        var progress = torrent?["progress"]?.GetValue<double>() ?? 0;
        var stateString = torrent?["state"]?.GetValue<string>() ?? string.Empty;
        if (progress >= 1.0)
        {
            return DownloadStatus.Completed;
        }

        return stateString switch
        {
            "error" => DownloadStatus.Failed,
            "missingFiles" => DownloadStatus.Failed,
            "uploading" => DownloadStatus.Completed,
            "pausedUP" => DownloadStatus.Completed,
            "queuedUP" => DownloadStatus.Completed,
            "stalledUP" => DownloadStatus.Completed,
            "checkingUP" => DownloadStatus.InProgress,
            "forcedUp" => DownloadStatus.Completed,
            "allocating" => DownloadStatus.InProgress,
            "downloading" => DownloadStatus.InProgress,
            "metaDL" => DownloadStatus.InProgress,
            "pausedDL" => DownloadStatus.Paused,
            "queuedDL" => DownloadStatus.Paused,
            "stalledDL" => DownloadStatus.Paused,
            "checkingDL" => DownloadStatus.InProgress,
            "forcedDL" => DownloadStatus.InProgress,
            "checkingResumeData" => DownloadStatus.InProgress,
            "moving" => DownloadStatus.InProgress,
            _ => DownloadStatus.Failed
        };
    }
}