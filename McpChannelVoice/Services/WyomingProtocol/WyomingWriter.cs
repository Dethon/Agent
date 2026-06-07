using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingWriter(Stream stream)
{
    private const string ProtocolVersion = "1.2";
    private static readonly byte[] _newline = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    // Not disposed by design: SemaphoreSlim only needs disposal when its AvailableWaitHandle is
    // used (it isn't here), and the writer lives for the whole connection.
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

        // Assemble the whole frame (header + newline + data + payload) into one contiguous buffer so
        // it goes out in a single write. Cancellation is honored at the lock; once we start emitting a
        // frame we finish it (CancellationToken.None) so a mid-frame cancel can't desync the stream.
        var frame = new byte[headerBytes.Length + _newline.Length + (dataBytes?.Length ?? 0) + evt.Payload.Length];
        var offset = 0;
        headerBytes.CopyTo(frame, offset);
        offset += headerBytes.Length;
        _newline.CopyTo(frame, offset);
        offset += _newline.Length;
        if (dataBytes is not null)
        {
            dataBytes.CopyTo(frame, offset);
            offset += dataBytes.Length;
        }
        if (evt.Payload.Length > 0)
        {
            evt.Payload.Span.CopyTo(frame.AsSpan(offset));
        }

        await _lock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(frame, CancellationToken.None);
            await stream.FlushAsync(CancellationToken.None);
        }
        finally
        {
            _lock.Release();
        }
    }
}