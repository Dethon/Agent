using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Infrastructure.Agents.ChatClients;

internal static class OpenRouterHttpHelpers
{
    private static readonly string[] _reasoningPropertyNames = ["reasoning", "reasoning_content", "thinking"];

    public static async Task FixEmptyAssistantContentWithToolCalls(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method != HttpMethod.Post ||
            request.Content?.Headers.ContentType?.MediaType?
                .Equals("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return;
        }

        // Some OpenRouter providers (e.g., Z.AI / GLM) reject assistant messages with tool_calls when content is empty
        var body = await request.Content.ReadAsStringAsync(ct);

        if (JsonNode.Parse(body) is not JsonObject obj || obj["messages"] is not JsonArray messages)
        {
            return;
        }

        foreach (var msg in messages.OfType<JsonObject>())
        {
            var content = msg["content"];
            switch (content)
            {
                case null:
                    continue;
                case JsonValue val when val.TryGetValue<string>(out var s) && string.IsNullOrEmpty(s):
                    msg.Remove("content");
                    break;
                case JsonArray arr:
                {
                    arr.RemoveAll(x => x is JsonObject itemObj &&
                                       itemObj["type"]?.GetValue<string>() == "text" &&
                                       string.IsNullOrEmpty(itemObj["text"]?.GetValue<string>()));

                    if (arr.Count == 0)
                    {
                        msg.Remove("content");
                    }

                    break;
                }
            }
        }

        request.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
    }

    public static HttpContent WrapWithReasoningTee(HttpContent inner, ConcurrentQueue<string> queue)
    {
        return new TeeHttpContent(inner, queue);
    }

    private static string? ExtractReasoningFromSseData(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice0 = choices[0];
            return GetReasoningFromElement(choice0, "delta") ?? GetReasoningFromElement(choice0, "message");
        }
        catch { return null; }
    }

    private static string? GetReasoningFromElement(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el) || el.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return _reasoningPropertyNames
            .Select(name => el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
                ? prop.GetString()
                : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed class TeeHttpContent(HttpContent inner, ConcurrentQueue<string> queue) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var innerStream = await inner.ReadAsStreamAsync();
            await innerStream.CopyToAsync(stream);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync()
        {
            return new ReasoningTeeStream(await inner.ReadAsStreamAsync(), queue);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ReasoningTeeStream(Stream inner, ConcurrentQueue<string> queue) : Stream
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _buffer = new();

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                ProcessBytes(buffer.AsSpan(offset, read));
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                ProcessBytes(buffer.Span[..read]);
            }

            return read;
        }

        private void ProcessBytes(ReadOnlySpan<byte> bytes)
        {
            try
            {
                Span<char> chars = stackalloc char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                _decoder.Convert(bytes, chars, flush: false, out _, out var charsUsed, out _);
                _buffer.Append(chars[..charsUsed]);

                var text = _buffer.ToString();
                if (!text.Contains('\n'))
                {
                    return;
                }

                var lines = text.Split('\n');
                _buffer.Clear().Append(lines[^1]);
                var reasoningLines = lines
                    .Take(lines.Length - 1)
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    .Select(l => l[5..].Trim())
                    .Where(d => d.Length > 0 && d != "[DONE]")
                    .Select(ExtractReasoningFromSseData)
                    .Where(r => r is not null);

                foreach (var reasoning in reasoningLines)
                {
                    queue.Enqueue(reasoning!);
                }
            }
            catch
            {
                // best-effort
            }
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}