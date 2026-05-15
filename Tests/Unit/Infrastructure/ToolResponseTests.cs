using System.Text.Json.Nodes;
using Domain.Exceptions;
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

    [Fact]
    public void Create_HomeAssistantNotFoundException_ProducesNotFoundNonRetryable()
    {
        var result = ToolResponse.Create(new HomeAssistantNotFoundException("x"));

        var first = result.Content[0].ShouldBeOfType<TextContentBlock>();
        var parsed = JsonNode.Parse(first.Text)!.AsObject();
        parsed["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        parsed["retryable"]!.GetValue<bool>().ShouldBe(false);
    }

    [Fact]
    public void Create_HomeAssistantUnauthorizedException_ProducesInvalidArgumentNonRetryable()
    {
        var result = ToolResponse.Create(new HomeAssistantUnauthorizedException("x"));

        var first = result.Content[0].ShouldBeOfType<TextContentBlock>();
        var parsed = JsonNode.Parse(first.Text)!.AsObject();
        parsed["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        parsed["retryable"]!.GetValue<bool>().ShouldBe(false);
    }

    // HA's REST validator returns 400 with a voluptuous error message ("expected list for
    // dictionary value @ data['cleaning_area_id']") when the agent passes the wrong shape.
    // Surfacing this as `internal_error` + `retryable:true` makes the agent re-try with
    // shuffled values; `invalid_argument` + `retryable:false` tells it to rethink instead.
    [Fact]
    public void Create_HomeAssistantException_400_ProducesInvalidArgumentNonRetryable()
    {
        var result = ToolResponse.Create(new HomeAssistantException("HA returned 400: bad input", 400));

        var first = result.Content[0].ShouldBeOfType<TextContentBlock>();
        var parsed = JsonNode.Parse(first.Text)!.AsObject();
        parsed["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        parsed["retryable"]!.GetValue<bool>().ShouldBe(false);
    }

    [Fact]
    public void Create_HomeAssistantException_500_RemainsInternalErrorRetryable()
    {
        var result = ToolResponse.Create(new HomeAssistantException("HA returned 500: integration crashed", 500));

        var first = result.Content[0].ShouldBeOfType<TextContentBlock>();
        var parsed = JsonNode.Parse(first.Text)!.AsObject();
        parsed["errorCode"]!.GetValue<string>().ShouldBe("internal_error");
        parsed["retryable"]!.GetValue<bool>().ShouldBe(true);
    }
}