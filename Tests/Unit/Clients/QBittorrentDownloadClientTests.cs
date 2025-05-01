using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Infrastructure.Clients;
using Moq;
using Moq.Protected;
using Shouldly;

namespace Tests.Unit.Clients;

public class QBittorrentDownloadClientTests
{
    private const string BaseUrl = "http://qbittorrent.test/api/v2/";
    private const string Username = "testuser";
    private const string Password = "testpassword";
    private const string TestLink = "magnet:?xt=urn:btih:123456";
    private const string TestSavePath = "/downloads/";
    private const int TestId = 123;
    private const string TestHash = "abcdef123456";

    private readonly Mock<HttpMessageHandler> _handlerMock;
    private readonly CookieContainer _cookieContainer;
    private readonly QBittorrentDownloadClient _client;

    public QBittorrentDownloadClientTests()
    {
        _handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        _cookieContainer = new CookieContainer();

        var httpClient = new HttpClient(_handlerMock.Object)
        {
            BaseAddress = new Uri(BaseUrl)
        };

        _client = new QBittorrentDownloadClient(httpClient, _cookieContainer, Username, Password);
    }

    [Fact]
    public async Task Download_ShouldSkipAuthentication_WhenAlreadyAuthenticated()
    {
        // given
        var baseUri = new Uri(BaseUrl);
        _cookieContainer.Add(baseUri, new Cookie("SID", "valid-session-id")
        {
            Expires = DateTime.Now.AddHours(1)
        });

        SetupAddTorrentResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash)]);

        // when
        await _client.Download(TestLink, TestSavePath, TestId);

        // then - verify auth was not called (implicit since we didn't set up the mock)
    }

    [Fact]
    public async Task Authenticate_ShouldThrowException_WhenAuthenticationFails()
    {
        // given
        SetupAuthenticationResponse(false);

        // when/then
        await Should.ThrowAsync<HttpRequestException>(() =>
            _client.Download(TestLink, TestSavePath, TestId));
    }

    [Fact]
    public async Task Download_ShouldSucceed_WhenTorrentIsAdded()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupAddTorrentResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash)]);

        // when/then
        await Should.NotThrowAsync(() => _client.Download(TestLink, TestSavePath, TestId));
    }

    [Fact]
    public async Task Download_ShouldThrowException_WhenAddTorrentFails()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupAddTorrentResponse(false);

        // when/then
        await Should.ThrowAsync<HttpRequestException>(() =>
            _client.Download(TestLink, TestSavePath, TestId));
    }

    [Fact]
    public async Task Download_ShouldThrowException_WhenTorrentNotFound()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupAddTorrentResponse(true);
        SetupGetTorrentsResponse([]);

        // when/then
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _client.Download(TestLink, TestSavePath, TestId));
    }

    [Fact]
    public async Task Cleanup_ShouldSucceed_WhenTorrentExists()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash)]);
        SetupDeleteTorrentResponse(true);

        // when/then
        await Should.NotThrowAsync(() => _client.Cleanup(TestId));
    }

    [Fact]
    public async Task Cleanup_ShouldDoNothing_WhenTorrentDoesNotExist()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([]);

        // when/then
        await Should.NotThrowAsync(() => _client.Cleanup(TestId));
    }

    [Fact]
    public async Task Cleanup_ShouldThrowException_WhenDeleteFails()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash)]);
        SetupDeleteTorrentResponse(false);

        // when/then
        await Should.ThrowAsync<HttpRequestException>(() => _client.Cleanup(TestId));
    }

    [Fact]
    public async Task IsDownloadComplete_ShouldReturnTrue_WhenDownloadIsComplete()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash)]);

        // when
        var result = await _client.IsDownloadComplete(TestId);

        // then
        result.ShouldBeTrue();
    }

    [Fact]
    public async Task IsDownloadComplete_ShouldReturnFalse_WhenDownloadIsInProgress()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash, 0.5, "downloading")]);

        // when
        var result = await _client.IsDownloadComplete(TestId);

        // then
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task IsDownloadComplete_ShouldReturnFalse_WhenTorrentNotFound()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([]);

        // when
        var result = await _client.IsDownloadComplete(TestId);

        // then
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task RefreshDownloadItems_ShouldUpdateStatus_ForExistingItems()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([
            CreateTorrentInfo("1", "hash1"),
            CreateTorrentInfo("2", "hash2", 0.5, "downloading")
        ]);

        var items = new[] { CreateDownloadItem(1), CreateDownloadItem(2), CreateDownloadItem(3) };

        // when
        var result = (await _client.RefreshDownloadItems(items)).ToArray();

        // then
        result.Length.ShouldBe(2);
        result.ShouldContain(x => x.Id == 1 && x.Status == DownloadStatus.Completed);
        result.ShouldContain(x => x.Id == 2 && x.Status == DownloadStatus.InProgress);
        result.ShouldNotContain(x => x.Id == 3);
    }

    [Fact]
    public async Task RefreshDownloadItems_ShouldReturnEmpty_WhenNoItemsProvided()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([CreateTorrentInfo($"{TestId}", TestHash)]);

        // when
        var result = await _client.RefreshDownloadItems([]);

        // then
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task RefreshDownloadItems_ShouldUpdateCorrectStatuses_ForDifferentTorrentStates()
    {
        // given
        SetupAuthenticationResponse(true);
        SetupGetTorrentsResponse([
            CreateTorrentInfo("1", "hash1"),
            CreateTorrentInfo("2", "hash2", 0.5, "error"),
            CreateTorrentInfo("3", "hash3", 0.3, "pausedDL"),
            CreateTorrentInfo("4", "hash4", 0.7, "downloading")
        ]);

        var items = new[]
        {
            CreateDownloadItem(1), CreateDownloadItem(2), CreateDownloadItem(3), CreateDownloadItem(4)
        };

        // when
        var result = (await _client.RefreshDownloadItems(items)).ToArray();

        // then
        result.Length.ShouldBe(4);
        result.ShouldContain(x => x.Id == 1 && x.Status == DownloadStatus.Completed);
        result.ShouldContain(x => x.Id == 2 && x.Status == DownloadStatus.Failed);
        result.ShouldContain(x => x.Id == 3 && x.Status == DownloadStatus.Paused);
        result.ShouldContain(x => x.Id == 4 && x.Status == DownloadStatus.InProgress);
    }

    #region Helper Methods

    private void SetupAuthenticationResponse(bool success)
    {
        var responseContent = success ? "Ok." : "Fails.";
        var statusCode = success ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;

        if (success)
        {
            var baseUri = new Uri(BaseUrl);
            _cookieContainer.Add(baseUri, new Cookie("SID", "test-session-id"));
        }

        SetupMockResponse(
            "auth/login",
            HttpMethod.Post,
            statusCode,
            responseContent,
            request => request.Content != null);
    }

    private void SetupAddTorrentResponse(bool success)
    {
        var responseContent = success ? "Ok." : "Fails.";
        var statusCode = success ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

        SetupMockResponse(
            "torrents/add",
            HttpMethod.Post,
            statusCode,
            responseContent,
            request => request.Content != null);
    }

    private void SetupDeleteTorrentResponse(bool success)
    {
        var responseContent = success ? "Ok." : "Fails.";
        var statusCode = success ? HttpStatusCode.OK : HttpStatusCode.BadRequest;

        SetupMockResponse(
            "torrents/delete",
            HttpMethod.Post,
            statusCode,
            responseContent,
            request => request.Content != null);
    }

    private void SetupGetTorrentsResponse(JsonObject[] torrents)
    {
        var responseContent = JsonSerializer.Serialize(torrents);

        SetupMockResponse(
            "torrents/info",
            HttpMethod.Get,
            HttpStatusCode.OK,
            responseContent);
    }

    private void SetupMockResponse(
        string relativeUri,
        HttpMethod method,
        HttpStatusCode statusCode,
        string content,
        Func<HttpRequestMessage, bool>? additionalCheck = null)
    {
        var absoluteUri = new Uri(new Uri(BaseUrl), relativeUri);

        _handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == method &&
                    req.RequestUri != null &&
                    req.RequestUri.AbsoluteUri == absoluteUri.AbsoluteUri &&
                    (additionalCheck == null || additionalCheck(req))),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
    }

    private static JsonObject CreateTorrentInfo(
        string name, string hash, double progress = 1.0, string state = "uploading")
    {
        return new JsonObject
        {
            ["name"] = name,
            ["hash"] = hash,
            ["progress"] = progress,
            ["state"] = state
        };
    }

    private static DownloadItem CreateDownloadItem(
        int id,
        string path = "test/path",
        string link = "magnet:?xt=urn:btih:test",
        string title = "TestTitle",
        DownloadStatus status = DownloadStatus.InProgress)
    {
        return new DownloadItem
        {
            Id = id,
            SavePath = path,
            Link = link,
            Title = title,
            Status = status
        };
    }

    #endregion
}