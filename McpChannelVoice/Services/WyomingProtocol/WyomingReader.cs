using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingReader(Stream stream)
{
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
            if (header["data_length"]?.GetValue<int>() is int dataLength && dataLength > 0)
            {
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
            if (header["payload_length"]?.GetValue<int>() is int payloadLength && payloadLength > 0)
            {
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