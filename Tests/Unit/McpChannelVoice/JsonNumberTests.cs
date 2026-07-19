using System.Text.Json.Nodes;
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class JsonNumberTests
{
    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void ReadInt_IntegerValue_ReturnsIt()
    {
        JsonNumber.ReadInt(Parse("""{"rate":16000}"""), "rate", 22050).ShouldBe(16000);
    }

    [Fact]
    public void ReadInt_FloatValue_RoundsInsteadOfThrowing()
    {
        // A non-conformant peer sending 16000.0 makes JsonValue.GetValue<int>() throw, which would
        // tear down the satellite connection mid-utterance. It must be parsed tolerantly instead.
        JsonNumber.ReadInt(Parse("""{"rate":16000.0}"""), "rate", 22050).ShouldBe(16000);
        JsonNumber.ReadInt(Parse("""{"rate":16000.6}"""), "rate", 22050).ShouldBe(16001);
    }

    [Fact]
    public void ReadInt_MissingKey_ReturnsFallback()
    {
        JsonNumber.ReadInt(Parse("""{"width":2}"""), "rate", 22050).ShouldBe(22050);
    }

    [Fact]
    public void ReadInt_OutOfRangeOrNonNumeric_ReturnsFallback()
    {
        JsonNumber.ReadInt(Parse("""{"rate":1e30}"""), "rate", 22050).ShouldBe(22050);
        JsonNumber.ReadInt(Parse("""{"rate":"loud"}"""), "rate", 22050).ShouldBe(22050);
    }

    [Fact]
    public void ReadLong_IntegerAndFloatValues_ParseTolerantly()
    {
        JsonNumber.ReadLong(Parse("""{"len":3}"""), "len", 0).ShouldBe(3);
        JsonNumber.ReadLong(Parse("""{"len":3.0}"""), "len", 0).ShouldBe(3);
        // Above int.MaxValue but within long: returned as-is so the caller's frame-size guard can reject it.
        JsonNumber.ReadLong(Parse("""{"len":99999999999}"""), "len", 0).ShouldBe(99999999999L);
    }

    [Fact]
    public void ReadLong_OversizedValue_ClampsSoTheGuardCanRejectIt()
    {
        // A present-but-out-of-long-range length must NOT silently fall back (which would skip the
        // frame and bypass the MaxFrameBytes guard); it is clamped to long.MaxValue so the guard trips.
        JsonNumber.ReadLong(Parse("""{"len":1e30}"""), "len", 0).ShouldBe(long.MaxValue);
    }

    [Fact]
    public void ReadLong_MissingOrNonNumeric_ReturnsFallback()
    {
        JsonNumber.ReadLong(Parse("""{"other":1}"""), "len", 0).ShouldBe(0);
        JsonNumber.ReadLong(Parse("""{"len":"big"}"""), "len", 0).ShouldBe(0);
    }

    [Fact]
    public void ReadDouble_FloatValue_ReturnsIt()
    {
        JsonNumber.ReadDouble(Parse("""{"score":0.42}"""), "score").ShouldBe(0.42);
    }

    [Fact]
    public void ReadDouble_IntegerValue_ReturnsIt()
    {
        // Whisper stats are floats, but a peer may serialize a whole number as an int.
        JsonNumber.ReadDouble(Parse("""{"score":1}"""), "score").ShouldBe(1.0);
    }

    [Fact]
    public void ReadDouble_MissingKey_ReturnsNull()
    {
        JsonNumber.ReadDouble(Parse("""{"text":"hola"}"""), "score").ShouldBeNull();
    }

    [Fact]
    public void ReadDouble_NonNumericValue_ReturnsNull()
    {
        // A malformed score must never throw: it would surface as an STT failure and drop the turn.
        JsonNumber.ReadDouble(Parse("""{"score":"high"}"""), "score").ShouldBeNull();
        JsonNumber.ReadDouble(Parse("""{"score":null}"""), "score").ShouldBeNull();
        JsonNumber.ReadDouble(Parse("""{"score":{"v":1}}"""), "score").ShouldBeNull();
    }
}