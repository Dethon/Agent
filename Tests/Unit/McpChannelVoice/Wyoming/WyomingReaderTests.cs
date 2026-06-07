using System.Text;
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingReaderTests
{
    [Fact]
    public async Task ReadAllAsync_HeaderOnly_YieldsEvent()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"describe\"}\n");
        await using var ms = new MemoryStream(bytes);
        var reader = new WyomingReader(ms);

        var events = new List<WyomingEvent>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Count.ShouldBe(1);
        events[0].Type.ShouldBe("describe");
        events[0].Payload.Length.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAllAsync_WithPayload_ReadsExactBytes()
    {
        var header = "{\"type\":\"audio-chunk\",\"payload_length\":3}\n";
        var payload = new byte[] { 9, 8, 7 };
        var combined = Encoding.UTF8.GetBytes(header).Concat(payload).ToArray();
        await using var ms = new MemoryStream(combined);

        var reader = new WyomingReader(ms);
        var events = new List<WyomingEvent>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Count.ShouldBe(1);
        events[0].Type.ShouldBe("audio-chunk");
        events[0].Payload.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task ReadAllAsync_FloatPayloadLength_ParsesTolerantlyInsteadOfThrowing()
    {
        // A non-conformant peer sending a JSON float length (e.g. 3.0) must not make the shared
        // reader throw the .NET 10 STJ InvalidOperationException (GetValue<int>() on a non-integral
        // Number) and tear the connection down — it should round to the intended integer.
        var header = "{\"type\":\"audio-chunk\",\"payload_length\":3.0}\n";
        var payload = new byte[] { 9, 8, 7 };
        var combined = Encoding.UTF8.GetBytes(header).Concat(payload).ToArray();
        await using var ms = new MemoryStream(combined);

        var reader = new WyomingReader(ms);
        var events = new List<WyomingEvent>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Count.ShouldBe(1);
        events[0].Payload.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task ReadAllAsync_OverLongDataLength_ThrowsInvalidDataNotInvalidOperation()
    {
        // An oversized length (> int.MaxValue) previously threw the STJ InvalidOperationException
        // BEFORE the MaxFrameBytes guard, defeating the DoS cap. It must instead hit the guard's
        // connection-loop-recoverable InvalidDataException.
        var header = "{\"type\":\"x\",\"data_length\":99999999999}\n";
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(header));
        var reader = new WyomingReader(ms);

        await Should.ThrowAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in reader.ReadAllAsync(CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task ReadAllAsync_MultipleEvents_YieldsInOrder()
    {
        var s = "{\"type\":\"a\"}\n{\"type\":\"b\",\"payload_length\":1}\n" + (char)42 + "{\"type\":\"c\"}\n";
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(s));
        var reader = new WyomingReader(ms);

        var types = new List<string>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            types.Add(evt.Type);
        }

        types.ShouldBe(["a", "b", "c"]);
    }
}