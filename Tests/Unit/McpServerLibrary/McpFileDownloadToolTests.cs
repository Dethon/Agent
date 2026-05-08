using McpServerLibrary.McpTools;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class McpFileDownloadToolTests
{
    [Fact]
    public void ValidateInputs_BothProvided_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: 1,
            link: "magnet:?xt=urn:btih:x",
            title: "x");

        result.ShouldNotBeNull();
        result["ok"]!.GetValue<bool>().ShouldBeFalse();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("either");
    }

    [Fact]
    public void ValidateInputs_NeitherProvided_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: null,
            title: null);

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
    }

    [Fact]
    public void ValidateInputs_LinkWithoutTitle_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: "magnet:?xt=urn:btih:x",
            title: null);

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("title");
    }

    [Fact]
    public void ValidateInputs_LinkWithBlankTitle_ReturnsInvalidArgument()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: "magnet:?xt=urn:btih:x",
            title: "   ");

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
    }

    [Theory]
    [InlineData("ftp://example.com/file.torrent")]
    [InlineData("/local/path/file.torrent")]
    [InlineData("just-some-text")]
    public void ValidateInputs_LinkWithDisallowedPrefix_ReturnsInvalidArgument(string link)
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: link,
            title: "Title");

        result.ShouldNotBeNull();
        result["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        result["message"]!.GetValue<string>().ShouldContain("magnet");
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:abc")]
    [InlineData("MAGNET:?xt=urn:btih:abc")]
    [InlineData("http://tracker.example.com/file.torrent")]
    [InlineData("https://tracker.example.com/file.torrent")]
    [InlineData("HTTPS://tracker.example.com/file.torrent")]
    public void ValidateInputs_AcceptedLinkWithTitle_ReturnsNull(string link)
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: null,
            link: link,
            title: "Title");

        result.ShouldBeNull();
    }

    [Fact]
    public void ValidateInputs_OnlySearchResultId_ReturnsNull()
    {
        var result = McpFileDownloadTool.ValidateInputs(
            searchResultId: 42,
            link: null,
            title: null);

        result.ShouldBeNull();
    }
}
