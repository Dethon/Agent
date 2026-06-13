using Domain.Tools.Downloads.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsPathTests
{
    [Theory]
    [InlineData("downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("/downloads/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("/downloads/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("", DownloadNodeKind.Other, null)]
    [InlineData("/", DownloadNodeKind.Other, null)]
    [InlineData("downloads", DownloadNodeKind.Other, null)]
    [InlineData("downloads/foo", DownloadNodeKind.Other, null)]
    [InlineData("downloads/42/payload.mkv", DownloadNodeKind.Other, null)]
    [InlineData("Movies/42", DownloadNodeKind.Other, null)]
    [InlineData("../downloads/42", DownloadNodeKind.Other, null)]
    public void Parse_ClassifiesPath_ReturnsKindAndId(string path, DownloadNodeKind kind, int? id)
    {
        var node = DownloadsPath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.Id.ShouldBe(id);
    }
}