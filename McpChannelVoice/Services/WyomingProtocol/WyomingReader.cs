using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingReader(Stream stream)
{
    // Upper bounds on header-supplied lengths so a malformed/corrupt frame can't drive an unbounded
    // allocation. Headers are tiny (~100 bytes); the largest legitimate payload is one audio chunk.
    private const int MaxHeaderBytes = 1 * 1024 * 1024;
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    public async IAsyncEnumerable<WyomingEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var headerBuffer = new MemoryStream();
        var oneByte = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            headerBuffer.SetLength(0);
            while (true)
            {
                var read = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct);
                if (read == 0)
                {
                    yield break;
                }
                if (oneByte[0] == (byte)'\n')
                {
                    break;
                }
                if (headerBuffer.Length >= MaxHeaderBytes)
                {
                    throw new InvalidDataException($"Wyoming header exceeds {MaxHeaderBytes} bytes without a newline");
                }
                headerBuffer.WriteByte(oneByte[0]);
            }

            if (headerBuffer.Length == 0)
            {
                continue;
            }

            var headerJson = Encoding.UTF8.GetString(headerBuffer.GetBuffer(), 0, (int)headerBuffer.Length);
            var header = JsonNode.Parse(headerJson)?.AsObject()
                         ?? throw new InvalidDataException("Wyoming header is not a JSON object");

            var type = header["type"]?.GetValue<string>()
                       ?? throw new InvalidDataException("Wyoming header missing 'type'");

            JsonObject data;
            // Parse length fields tolerantly (a non-conformant peer may send a JSON float/oversized
            // number, which JsonValue.GetValue<int>() throws on — and that throw would fire BEFORE
            // the MaxFrameBytes guard below, defeating it). ReadLong clamps oversized values so they
            // still hit the guard's recoverable InvalidDataException instead of an STJ throw.
            var dataLength = WyomingNumber.ReadLong(header, "data_length", 0);
            if (dataLength > 0)
            {
                if (dataLength > MaxFrameBytes)
                {
                    throw new InvalidDataException($"Wyoming data_length {dataLength} exceeds {MaxFrameBytes}");
                }
                var dataBuf = new byte[dataLength];
                await ReadExactAsync(stream, dataBuf, ct);
                var dataJson = Encoding.UTF8.GetString(dataBuf);
                data = JsonNode.Parse(dataJson)?.AsObject() ?? new JsonObject();
            }
            else if (header["data"] is JsonNode inline && inline is JsonObject inlineObj)
            {
                data = inlineObj.DeepClone().AsObject();
            }
            else
            {
                data = new JsonObject();
            }

            ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
            var payloadLength = WyomingNumber.ReadLong(header, "payload_length", 0);
            if (payloadLength > 0)
            {
                if (payloadLength > MaxFrameBytes)
                {
                    throw new InvalidDataException($"Wyoming payload_length {payloadLength} exceeds {MaxFrameBytes}");
                }
                var payloadBuf = new byte[payloadLength];
                await ReadExactAsync(stream, payloadBuf, ct);
                payload = payloadBuf;
            }

            yield return new WyomingEvent(type, data, payload);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct);
            if (read == 0)
            {
                throw new EndOfStreamException("Stream closed mid-payload");
            }
            totalRead += read;
        }
    }
}