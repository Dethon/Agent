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
        await CallApi(c => AddTorrent($"{id}", link, savePath, c), cancellationToken);
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(500, cancellationToken);
            if (await GetSingleTorrent($"{id}", cancellationToken) is not null)
            {
                return;
            }
        }

        throw new InvalidOperationException("Torrent cannot be added, try another link. Search again if necessary");
    }

    public async Task Cleanup(int id, CancellationToken cancellationToken = default)
    {
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

        await CallApi(c => RemoveTorrent(hash, c), cancellationToken);
    }

    public async Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default)
    {
        var torrent = await GetSingleTorrent($"{id}", cancellationToken);
        if (torrent is null)
        {
            return null;
        }

        return new DownloadItem
        {
            Id = id,
            Title = torrent["name"]?.GetValue<string>() ?? string.Empty,
            Size = torrent["total_size"]?.GetValue<long>() ?? 0,
            Status = GetDownloadStatus(torrent),
            Seeders = torrent["num_seeds"]?.GetValue<int>() ?? 0,
            Peers = torrent["num_leechs"]?.GetValue<int>() ?? 0,
            SavePath = torrent["save_path"]?.GetValue<string>() ?? string.Empty,
            Link = torrent["magnet_uri"]?.GetValue<string>() ?? string.Empty,
            Progress = (torrent["progress"]?.GetValue<double>() ?? 0.0) * 100,
            DownSpeed = (torrent["dlspeed"]?.GetValue<long>() ?? 0) / 1024 / 1024,
            UpSpeed = (torrent["upspeed"]?.GetValue<long>() ?? 0) / 1024 / 1024
        };
    }

    private async Task<JsonNode?> GetSingleTorrent(string id, CancellationToken cancellationToken)
    {
        var torrents = await CallApi(GetAllTorrents, cancellationToken);
        return torrents.SingleOrDefault(x =>
        {
            var name = x["name"]?.GetValue<string>() ?? string.Empty;
            return name.Equals(id, StringComparison.OrdinalIgnoreCase);
        });
    }

    private async Task<T> CallApi<T>(Func<CancellationToken, Task<T>> func, CancellationToken cancellationToken)
    {
        await Authenticate(cancellationToken);
        try
        {
            return await func(cancellationToken);
        }
        catch (HttpRequestException e)
        {
            if (e.StatusCode != HttpStatusCode.Forbidden)
            {
                throw;
            }

            await Authenticate(cancellationToken, true);
            return await func(cancellationToken);
        }
    }

    private async Task<JsonNode[]> GetAllTorrents(CancellationToken cancellationToken)
    {
        var response = await client.GetAsync("torrents/info", cancellationToken);
        response.EnsureSuccessStatusCode();
        var torrents = await response.Content.ReadFromJsonAsync<JsonNode[]>(cancellationToken);
        return torrents ?? [];
    }

    private async Task<bool> AddTorrent(string id, string link, string savePath, CancellationToken cancellationToken)
    {
        var addTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("urls", link),
            new KeyValuePair<string, string>("savepath", savePath),
            new KeyValuePair<string, string>("rename", $"{id}")
        ]);
        var response = await client.PostAsync("torrents/add", addTorrentContent, cancellationToken);
        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task<bool> RemoveTorrent(string hash, CancellationToken cancellationToken)
    {
        var deleteTorrentContent = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("hashes", hash),
            new KeyValuePair<string, string>("deleteFiles", "true")
        ]);
        var response = await client.PostAsync("torrents/delete", deleteTorrentContent, cancellationToken);
        response.EnsureSuccessStatusCode();
        return true;
    }

    private async Task Authenticate(CancellationToken cancellationToken, bool force = false)
    {
        if (!force && IsAuthenticated())
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