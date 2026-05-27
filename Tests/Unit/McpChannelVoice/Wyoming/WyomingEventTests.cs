using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingEventTests
{
    [Fact]
    public void Create_WithDataOnly_HasNoPayload()
    {
        var data = new JsonObject { ["text"] = "hello" };
        var evt = new WyomingEvent("transcript", data, ReadOnlyMemory<byte>.Empty);
        evt.Type.ShouldBe("transcript");
        evt.Payload.Length.ShouldBe(0);
    }

    [Fact]
    public void Create_WithPayload_PreservesBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var evt = new WyomingEvent("audio-chunk", new JsonObject(), bytes);
        evt.Payload.ToArray().ShouldBe(bytes);
    }
}