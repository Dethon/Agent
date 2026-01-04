using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Infrastructure.Agents.ChatClients;

internal static class OpenRouterReasoningTap
{
    public static readonly AsyncLocal<ConcurrentQueue<string>?> CurrentQueue = new();
}

internal sealed class OpenRouterReasoningHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await TryFixEmptyAssistantContentWithToolCalls(request, cancellationToken);

        var response = await base.SendAsync(request, cancellationToken);

        var queue = OpenRouterReasoningTap.CurrentQueue.Value;
        if (queue is null)
        {
            return response;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        response.Content = new TeeHttpContent(response.Content, queue);
        return response;
    }

    private static async Task TryFixEmptyAssistantContentWithToolCalls(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        if (request.Content is null)
        {
            return;
        }

        var contentType = request.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Some OpenRouter providers (e.g., Z.AI / GLM) reject assistant messages that include tool_calls
        // when the text content part exists but is empty.
        var body = await request.Content.ReadAsStringAsync(ct);
        if (!body.Contains("\"tool_calls\"", StringComparison.Ordinal))
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch
        {
            return;
        }

        if (root is not JsonObject obj || obj["messages"] is not JsonArray messages)
        {
            return;
        }

        var changed = false;
        foreach (var node in messages)
        {
            if (node is not JsonObject msg)
            {
                continue;
            }

            var hasToolCalls = msg["tool_calls"] is not null || msg["function_call"] is not null;
            if (!hasToolCalls)
            {
                continue;
            }

            if (msg["content"] is JsonValue v && v.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
            {
                msg.Remove("content");
                changed = true;
                continue;
            }

            if (msg["content"] is JsonArray parts)
            {
                var allEmptyText = parts.Count > 0 && parts
                    .OfType<JsonObject>()
                    .All(p => string.IsNullOrWhiteSpace(p["text"]?.GetValue<string>()));

                if (allEmptyText)
                {
                    msg.Remove("content");
                    changed = true;
                }
            }
        }

        if (!changed)
        {
            return;
        }

        request.Content = new StringContent(obj.ToJsonString(), Encoding.UTF8, "application/json");
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
            var innerStream = await inner.ReadAsStreamAsync();
            return new ReasoningTeeStream(innerStream, queue);
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

                while (true)
                {
                    var s = _buffer.ToString();
                    var nl = s.IndexOf('\n');
                    if (nl < 0)
                    {
                        return;
                    }

                    var line = s[..nl].TrimEnd('\r');
                    _buffer.Clear();
                    _buffer.Append(s[(nl + 1)..]);

                    if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var data = line[5..].Trim();
                    if (data.Length == 0 || data == "[DONE]")
                    {
                        continue;
                    }

                    TryExtractReasoningFromData(data);
                }
            }
            catch
            {
                // best-effort
            }
        }

        private void TryExtractReasoningFromData(string data)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    return;
                }

                var choice0 = choices[0];

                // Streaming shape: choices[0].delta.reasoning / reasoning_content
                if (choice0.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    var r = GetStringProp(delta, "reasoning") ??
                            GetStringProp(delta, "reasoning_content") ?? GetStringProp(delta, "thinking");
                    if (!string.IsNullOrWhiteSpace(r))
                    {
                        queue.Enqueue(r);
                    }
                }

                // Non-streaming-ish updates: choices[0].message.reasoning
                if (choice0.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
                {
                    var r = GetStringProp(message, "reasoning") ?? GetStringProp(message, "reasoning_content") ??
                        GetStringProp(message, "thinking");
                    if (!string.IsNullOrWhiteSpace(r))
                    {
                        queue.Enqueue(r);
                    }
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static string? GetStringProp(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var el))
            {
                return null;
            }

            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                _ => null
            };
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
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

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}