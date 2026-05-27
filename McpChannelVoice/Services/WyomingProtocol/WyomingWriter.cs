using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingWriter(Stream stream)
{
    private static readonly byte[] _newline = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(WyomingEvent evt, CancellationToken ct)
    {
        var header = new JsonObject(evt.Data.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone()))
        {
            ["type"] = evt.Type
        };
        if (evt.Payload.Length > 0)
        {
            header["payload_length"] = evt.Payload.Length;
        }

        var bytes = Encoding.UTF8.GetBytes(header.ToJsonString(_serializerOptions));

        await _lock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.WriteAsync(_newline, ct);
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