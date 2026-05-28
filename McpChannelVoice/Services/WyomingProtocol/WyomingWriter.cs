using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingWriter(Stream stream)
{
    private const string ProtocolVersion = "1.2";
    private static readonly byte[] _newline = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(WyomingEvent evt, CancellationToken ct)
    {
        byte[]? dataBytes = null;
        if (evt.Data.Count > 0)
        {
            dataBytes = Encoding.UTF8.GetBytes(evt.Data.ToJsonString(_serializerOptions));
        }

        var header = new JsonObject
        {
            ["type"] = evt.Type,
            ["version"] = ProtocolVersion
        };
        if (dataBytes is not null)
        {
            header["data_length"] = dataBytes.Length;
        }
        if (evt.Payload.Length > 0)
        {
            header["payload_length"] = evt.Payload.Length;
        }

        var headerBytes = Encoding.UTF8.GetBytes(header.ToJsonString(_serializerOptions));

        await _lock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(headerBytes, ct);
            await stream.WriteAsync(_newline, ct);
            if (dataBytes is not null)
            {
                await stream.WriteAsync(dataBytes, ct);
            }
            if (evt.Payload.Length > 0)
            {
                await stream.WriteAsync(evt.Payload, ct);
            }
            await stream.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}