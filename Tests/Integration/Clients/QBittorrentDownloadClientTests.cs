using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

public class QBittorrentDownloadClientTests(QBittorrentFixture fixture) : IClassFixture<QBittorrentFixture>
{
    [Fact]
    public async Task GetDownloadItem_WhenTorrentDoesNotExist_ReturnsNull()
    {
        // Arrange
        var client = fixture.CreateClient();
        const int nonExistentId = 999999;

        // Act
        var result = await client.GetDownloadItem(nonExistentId, CancellationToken.None);

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public async Task Cleanup_WhenTorrentDoesNotExist_DoesNotThrow()
    {
        // Arrange
        var client = fixture.CreateClient();
        const int nonExistentId = 999998;

        // Act & Assert - should not throw
        await Should.NotThrowAsync(() => client.Cleanup(nonExistentId, CancellationToken.None));
    }

    [Fact]
    public async Task Download_WithValidMagnetLink_AddsTorrentSuccessfully()
    {
        // Arrange
        var client = fixture.CreateClient();
        // Ubuntu 24.04 - a well-seeded public domain torrent
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";
        const string savePath = "/downloads";
        var id = new Random().Next(100000, 999999);

        try
        {
            // Act
            await client.Download(magnetLink, savePath, id, CancellationToken.None);

            // Assert - verify it was added
            var downloadItem = await client.GetDownloadItem(id, CancellationToken.None);
            downloadItem.ShouldNotBeNull();
            downloadItem.Id.ShouldBe(id);
        }
        finally
        {
            // Cleanup
            await client.Cleanup(id, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Download_AndCleanup_RemovesTorrent()
    {
        // Arrange
        var client = fixture.CreateClient();
        // Ubuntu 24.04 - a well-seeded public domain torrent
        const string magnetLink =
            "magnet:?xt=urn:btih:KRWPCX3SJUM4IMM4YF3MVSJIBFTHVFCS&dn=ubuntu-24.04-desktop-amd64.iso";
        const string savePath = "/downloads";
        var id = new Random().Next(100000, 999999);

        // Act - Add torrent
        await client.Download(magnetLink, savePath, id, CancellationToken.None);

        // Verify it exists
        var downloadItem = await client.GetDownloadItem(id, CancellationToken.None);
        downloadItem.ShouldNotBeNull();

        // Act - Cleanup
        await client.Cleanup(id, CancellationToken.None);

        // Assert - Should be removed
        var afterCleanup = await client.GetDownloadItem(id, CancellationToken.None);
        afterCleanup.ShouldBeNull();
    }
}