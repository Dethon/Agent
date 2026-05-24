using Domain.DTOs.FileSystem;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.FileSystem;

public class FsResultTests
{
    [Fact]
    public void Ok_TryGetValue_ReturnsValue()
    {
        var dto = new FsGlobResult { Entries = ["a"], Truncated = false, Total = 1 };
        FsResult<FsGlobResult> result = new FsResult<FsGlobResult>.Ok(dto);

        result.TryGetValue(out var value, out var error).ShouldBeTrue();
        value.ShouldBe(dto);
        error.ShouldBeNull();
    }

    [Fact]
    public void Err_TryGetValue_ReturnsError()
    {
        var err = new ToolErrorResult { ErrorCode = ToolError.Codes.NotFound, Message = "missing", Retryable = false };
        FsResult<FsGlobResult> result = new FsResult<FsGlobResult>.Err(err);

        result.TryGetValue(out var value, out var error).ShouldBeFalse();
        value.ShouldBeNull();
        error.ShouldBe(err);
    }

    [Fact]
    public void Ok_ToNode_SerializesDtoWithContractOptions()
    {
        var dto = new FsGlobResult { Entries = ["a"], Truncated = false, Total = 1 };
        FsResult<FsGlobResult> result = new FsResult<FsGlobResult>.Ok(dto);

        var node = result.ToNode();

        node["entries"]!.AsArray().Count.ShouldBe(1);
        node["truncated"]!.GetValue<bool>().ShouldBeFalse();
        node["total"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public void Err_ToNode_EmitsEnvelope()
    {
        var err = new ToolErrorResult { ErrorCode = ToolError.Codes.NotFound, Message = "missing", Retryable = false };
        FsResult<FsGlobResult> result = new FsResult<FsGlobResult>.Err(err);

        var node = result.ToNode();

        node["ok"]!.GetValue<bool>().ShouldBeFalse();
        node["errorCode"]!.GetValue<string>().ShouldBe("not_found");
    }
}