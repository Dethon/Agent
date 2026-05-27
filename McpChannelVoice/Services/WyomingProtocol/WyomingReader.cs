using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingReader(Stream stream)
{
    public async IAsyncEnumerable<WyomingEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var oneByte = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            buffer.SetLength(0);
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
                buffer.WriteByte(oneByte[0]);
            }

            if (buffer.Length == 0)
            {
                continue;
            }

            var headerJson = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var node = JsonNode.Parse(headerJson)?.AsObject()
                       ?? throw new InvalidDataException("Wyoming header is not a JSON object");

            var type = node["type"]?.GetValue<string>()
                       ?? throw new InvalidDataException("Wyoming header missing 'type'");

            ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
            if (node["payload_length"]?.GetValue<int>() is int payloadLength && payloadLength > 0)
            {
                var payloadBuf = new byte[payloadLength];
                var totalRead = 0;
                while (totalRead < payloadLength)
                {
                    var read = await stream.ReadAsync(
                        payloadBuf.AsMemory(totalRead, payloadLength - totalRead), ct);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Stream closed mid-payload");
                    }
                    totalRead += read;
                }
                payload = payloadBuf;
                node.Remove("payload_length");
            }
            node.Remove("type");

            yield return new WyomingEvent(type, node, payload);
        }
    }
}