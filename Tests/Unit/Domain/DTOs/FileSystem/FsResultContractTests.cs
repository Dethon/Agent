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

    [Theory]
    [InlineData("fs_read")]
    [InlineData("fs_info")]
    [InlineData("fs_glob")]
    [InlineData("fs_search")]
    [InlineData("fs_exec")]
    [InlineData("fs_create")]
    [InlineData("fs_edit")]
    [InlineData("fs_move")]
    [InlineData("fs_delete")]
    [InlineData("fs_copy")]
    [InlineData("fs_blob_read")]
    [InlineData("fs_blob_write")]
    public void ResultTypes_CoversEveryFsTool(string toolName)
    {
        FsResultContract.ResultTypes.ShouldContainKey(toolName);
    }

    [Fact]
    public void CreateResult_OmitsNote_WhenNull_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = "/vault/a.md", Size = "3 B", Lines = 1
        });

        node.ToJsonString().ShouldNotContain("note");
        FsResultContract.TryValidate("fs_create", node, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void CreateResult_IncludesNote_WhenSet_AndValidates()
    {
        var node = FsResultContract.ToNode(new FsCreateResult
        {
            Status = "created", FilePath = "/vault/a.md", Size = "3 B", Lines = 1, Note = "coerced"
        });

        node.ToJsonString().ShouldContain("\"note\":\"coerced\"");
        FsResultContract.TryValidate("fs_create", node, out _).ShouldBeTrue();
    }

    [Fact]
    public void EditResult_WithNote_Validates()
    {
        var node = FsResultContract.ToNode(new FsEditResult
        {
            Status = "edited", FilePath = "/vault/a.md", TotalOccurrencesReplaced = 1,
            Edits = [new FsEditDetail { OccurrencesReplaced = 1, AffectedLines = new FsLineRange { Start = 1, End = 1 } }],
            Note = "coerced"
        });

        node.ToJsonString().ShouldContain("\"note\":\"coerced\"");
        FsResultContract.TryValidate("fs_edit", node, out _).ShouldBeTrue();
    }

    private static JsonNode JsonNodeWith(string json) => JsonNode.Parse(json)!;
}