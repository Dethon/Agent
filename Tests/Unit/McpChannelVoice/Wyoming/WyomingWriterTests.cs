using System.Text;
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingWriterTests
{
    [Fact]
    public async Task WriteAsync_HeaderOnly_WritesJsonLine()
    {
        await using var ms = new MemoryStream();
        var writer = new WyomingWriter(ms);

        await writer.WriteAsync(
            WyomingEvent.Header("describe", new JsonObject()),
            CancellationToken.None);

        var output = Encoding.UTF8.GetString(ms.ToArray());
        output.ShouldEndWith("\n");
        output.ShouldContain("\"type\":\"describe\"");
    }

    [Fact]
    public async Task WriteAsync_WithPayload_AppendsBytesAfterNewline()
    {
        await using var ms = new MemoryStream();
        var writer = new WyomingWriter(ms);
        var payload = new byte[] { 1, 2, 3, 4 };
        var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };

        await writer.WriteAsync(
            WyomingEvent.WithPayload("audio-chunk", data, payload),
            CancellationToken.None);

        var bytes = ms.ToArray();
        var newlineIndex = Array.IndexOf(bytes, (byte)'\n');
        newlineIndex.ShouldBeGreaterThan(0);
        var header = Encoding.UTF8.GetString(bytes, 0, newlineIndex);
        header.ShouldContain("\"payload_length\":4");
        bytes[(newlineIndex + 1)..].ShouldBe(payload);
    }
}