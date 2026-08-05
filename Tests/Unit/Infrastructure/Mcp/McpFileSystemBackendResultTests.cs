using Domain.Tools;
using Infrastructure.Agents.Mcp;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

// The backend promises the error envelope at every exit. The shipped servers' call-tool filter
// always answers JSON, but an SDK-generated rejection or a third-party server can answer plain
// text (or nothing at all), and parsing that before looking at IsError threw a raw JsonException
// out of the very layer that exists to prevent it.
public class McpFileSystemBackendResultTests
{
    private readonly McpFileSystemBackend _backend = new(null!, "test");

    private static CallToolResult Result(bool isError, params string[] texts) => new()
    {
        IsError = isError,
        Content = texts.Select(ContentBlock (t) => new TextContentBlock { Text = t }).ToList()
    };

    [Fact]
    public void InterpretResult_ServerErrorWithPlainTextContent_BecomesTheErrorEnvelope()
    {
        var node = _backend.InterpretResult(Result(isError: true, "tool blew up"), "fs_glob");

        node["ok"]!.GetValue<bool>().ShouldBeFalse();
        node["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InternalError);
        node["message"]!.GetValue<string>().ShouldContain("tool blew up");
    }

    [Fact]
    public void InterpretResult_ServerErrorWithNoTextContent_BecomesTheErrorEnvelope()
    {
        var node = _backend.InterpretResult(Result(isError: true), "fs_glob");

        node["ok"]!.GetValue<bool>().ShouldBeFalse();
        node["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InternalError);
    }

    [Fact]
    public void InterpretResult_SuccessWithNonJsonText_BecomesTheMalformedPayloadEnvelope()
    {
        var node = _backend.InterpretResult(Result(isError: false, "not json"), "fs_glob");

        node["ok"]!.GetValue<bool>().ShouldBeFalse();
        node["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.InternalError);
    }

    [Fact]
    public void InterpretResult_ServerErrorEnvelope_PassesThroughUnchanged()
    {
        var envelope = """{"ok":false,"errorCode":"not_found","message":"no such path","retryable":false}""";

        var node = _backend.InterpretResult(Result(isError: true, envelope), "fs_glob");

        node["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.NotFound);
        node["message"]!.GetValue<string>().ShouldBe("no such path");
    }

    [Fact]
    public void InterpretResult_ValidPayload_IsReturned()
    {
        var node = _backend.InterpretResult(
            Result(isError: false, """{"entries":[],"truncated":false,"total":0}"""), "fs_glob");

        node["entries"].ShouldNotBeNull();
    }
}