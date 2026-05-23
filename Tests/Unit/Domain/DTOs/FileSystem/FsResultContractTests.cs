using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsResultContractTests
{
    [Fact]
    public void ToNode_SerializesReadResult_WithCamelCaseAndOmittedNulls()
    {
        var node = FsResultContract.ToNode(new FsReadResult
        {
            FilePath = "/vault/a.md",
            Content = "1: hi",
            TotalLines = 1,
            Truncated = false
        });

        var json = node.ToJsonString();
        json.ShouldContain("\"filePath\":\"/vault/a.md\"");
        json.ShouldContain("\"totalLines\":1");
        json.ShouldNotContain("suggestion");
    }

    [Fact]
    public void TryValidate_AcceptsConformingPayload()
    {
        var node = FsResultContract.ToNode(new FsReadResult
        {
            FilePath = "/vault/a.md", Content = "x", TotalLines = 1, Truncated = false
        });

        FsResultContract.TryValidate("fs_read", node, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidate_RejectsExtraMember()
    {
        var node = JsonNodeWith("{\"filePath\":\"a\",\"content\":\"x\",\"totalLines\":1,\"truncated\":false,\"bogus\":true}");

        FsResultContract.TryValidate("fs_read", node, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void TryValidate_RejectsMissingRequiredMember()
    {
        var node = JsonNodeWith("{\"filePath\":\"a\",\"content\":\"x\"}");

        FsResultContract.TryValidate("fs_read", node, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_SkipsUnknownTool()
    {
        var node = JsonNodeWith("{\"anything\":1}");

        FsResultContract.TryValidate("fs_not_a_tool", node, out _).ShouldBeTrue();
    }

    private static JsonNode JsonNodeWith(string json) => JsonNode.Parse(json)!;
}