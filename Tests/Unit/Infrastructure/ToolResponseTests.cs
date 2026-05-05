using System.Text.Json.Nodes;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ToolResponseTests
{
    [Fact]
    public void Create_WithBody_EmitsTwoTextBlocks_BodyRaw()
    {
        var envelope = new JsonObject
        {
            ["status"] = "success",
            ["url"] = "https://example.com"
        };
        var body = "# Heading\n\nLine with \"quotes\" and a backslash \\.";

        var result = ToolResponse.Create(envelope, body);

        result.IsError.ShouldBe(false);
        result.Content.Count.ShouldBe(2);

        var first = result.Content[0].ShouldBeOfType<TextContentBlock>();
        var second = result.Content[1].ShouldBeOfType<TextContentBlock>();

        var parsedEnvelope = JsonNode.Parse(first.Text)!.AsObject();
        parsedEnvelope["status"]!.GetValue<string>().ShouldBe("success");
        parsedEnvelope["url"]!.GetValue<string>().ShouldBe("https://example.com");

        second.Text.ShouldBe(body);
    }

    [Fact]
    public void Create_WithBody_EnvelopeWithOkFalse_SetsIsError()
    {
        var envelope = new JsonObject
        {
            ["ok"] = false,
            ["error"] = new JsonObject { ["code"] = "internal_error" }
        };

        var result = ToolResponse.Create(envelope, "details");

        result.IsError.ShouldBe(true);
    }
}
