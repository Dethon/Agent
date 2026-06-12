using Domain.Tools.Downloads.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsPathTests
{
    [Theory]
    [InlineData("", DownloadNodeKind.Root, null)]
    [InlineData("/", DownloadNodeKind.Root, null)]
    [InlineData("42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("/42", DownloadNodeKind.DownloadDir, 42)]
    [InlineData("42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("/42/status.json", DownloadNodeKind.StatusFile, 42)]
    [InlineData("foo", DownloadNodeKind.Unknown, null)]
    [InlineData("../42", DownloadNodeKind.Unknown, null)]
    [InlineData("42/other.txt", DownloadNodeKind.Unknown, null)]
    public void Parse_ClassifiesPath_ReturnsKindAndId(string path, DownloadNodeKind kind, int? id)
    {
        var node = DownloadsPath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.Id.ShouldBe(id);
    }
}