using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingNumberTests
{
    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void ReadInt_IntegerValue_ReturnsIt()
    {
        WyomingNumber.ReadInt(Parse("""{"rate":16000}"""), "rate", 22050).ShouldBe(16000);
    }

    [Fact]
    public void ReadInt_FloatValue_RoundsInsteadOfThrowing()
    {
        // A non-conformant peer sending 16000.0 makes JsonValue.GetValue<int>() throw, which would
        // tear down the satellite connection mid-utterance. It must be parsed tolerantly instead.
        WyomingNumber.ReadInt(Parse("""{"rate":16000.0}"""), "rate", 22050).ShouldBe(16000);
        WyomingNumber.ReadInt(Parse("""{"rate":16000.6}"""), "rate", 22050).ShouldBe(16001);
    }

    [Fact]
    public void ReadInt_MissingKey_ReturnsFallback()
    {
        WyomingNumber.ReadInt(Parse("""{"width":2}"""), "rate", 22050).ShouldBe(22050);
    }

    [Fact]
    public void ReadInt_OutOfRangeOrNonNumeric_ReturnsFallback()
    {
        WyomingNumber.ReadInt(Parse("""{"rate":1e30}"""), "rate", 22050).ShouldBe(22050);
        WyomingNumber.ReadInt(Parse("""{"rate":"loud"}"""), "rate", 22050).ShouldBe(22050);
    }
}