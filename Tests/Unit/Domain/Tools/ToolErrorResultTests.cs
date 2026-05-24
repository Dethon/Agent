using System.Text.Json.Nodes;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Domain.Tools;

public class ToolErrorResultTests
{
    [Fact]
    public void ToNode_HintProvided_EmitsHintField()
    {
        var node = new ToolErrorResult
        {
            ErrorCode = ToolError.Codes.NotFound,
            Message = "missing",
            Retryable = true,
            Hint = "try again"
        }.ToNode();

        node["ok"]!.GetValue<bool>().ShouldBeFalse();
        node["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        node["message"]!.GetValue<string>().ShouldBe("missing");
        node["retryable"]!.GetValue<bool>().ShouldBeTrue();
        node["hint"]!.GetValue<string>().ShouldBe("try again");
    }

    [Fact]
    public void ToNode_HintNull_OmitsHintField()
    {
        var node = new ToolErrorResult { ErrorCode = "x", Message = "m", Retryable = false }.ToNode();

        node.ContainsKey("hint").ShouldBeFalse();
    }

    [Fact]
    public void IsErrorEnvelope_VariousNodes_TrueOnlyWhenOkFalse()
    {
        ToolErrorResult.IsErrorEnvelope(new JsonObject { ["ok"] = false }).ShouldBeTrue();
        ToolErrorResult.IsErrorEnvelope(new JsonObject { ["ok"] = true }).ShouldBeFalse();
        ToolErrorResult.IsErrorEnvelope(new JsonObject { ["content"] = "x" }).ShouldBeFalse();
        ToolErrorResult.IsErrorEnvelope(null).ShouldBeFalse();
    }

    [Fact]
    public void FromEnvelope_ErrorEnvelope_RoundTripsFields()
    {
        var envelope = ToolError.Create(ToolError.Codes.Timeout, "slow", retryable: true, hint: "wait");

        var parsed = ToolErrorResult.FromEnvelope(envelope);

        parsed.ShouldNotBeNull();
        parsed!.ErrorCode.ShouldBe("timeout");
        parsed.Message.ShouldBe("slow");
        parsed.Retryable.ShouldBeTrue();
        parsed.Hint.ShouldBe("wait");
    }

    [Fact]
    public void FromEnvelope_RetryableFalse_RoundTrips()
    {
        var envelope = ToolError.Create(ToolError.Codes.NotFound, "gone", retryable: false);
        var parsed = ToolErrorResult.FromEnvelope(envelope);
        parsed.ShouldNotBeNull();
        parsed!.Retryable.ShouldBeFalse();
    }

    [Fact]
    public void FromEnvelope_SuccessNode_ReturnsNull()
    {
        ToolErrorResult.FromEnvelope(new JsonObject { ["content"] = "x" }).ShouldBeNull();
    }

    [Fact]
    public void Create_ValidArgs_ProducesEnvelope()
    {
        var node = ToolError.Create(ToolError.Codes.InvalidArgument, "bad", retryable: false);

        node["ok"]!.GetValue<bool>().ShouldBeFalse();
        node["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        node["message"]!.GetValue<string>().ShouldBe("bad");
        node["retryable"]!.GetValue<bool>().ShouldBeFalse();
        node.ContainsKey("hint").ShouldBeFalse();
    }
}