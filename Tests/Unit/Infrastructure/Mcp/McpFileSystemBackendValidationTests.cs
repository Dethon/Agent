using System.Text.Json.Nodes;
using Domain.DTOs.FileSystem;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpFileSystemBackendValidationTests
{
    [Fact]
    public void TryValidate_MalformedReadPayload_IsRejected()
    {
        var malformed = JsonNode.Parse("{\"filePath\":\"a\",\"content\":\"x\",\"totalLines\":1,\"truncated\":false,\"bogus\":1}")!;

        FsResultContract.TryValidate("fs_read", malformed, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
    }

    [Fact]
    public void ConformingPayload_PassesValidation()
    {
        var ok = FsResultContract.ToNode(new FsGlobResult { Entries = ["a"], Truncated = false, Total = 1 });

        FsResultContract.TryValidate("fs_glob", ok, out _).ShouldBeTrue();
    }
}