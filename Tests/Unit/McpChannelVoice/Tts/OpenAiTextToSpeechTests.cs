using System.Net;
using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class OpenAiTextToSpeechTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    // Serves one scripted byte[] segment per Read call so the test controls exactly how the
    // PCM body is sliced across reads (including mid-sample splits).
    private sealed class ScriptedStream(IReadOnlyList<byte[]> segments) : Stream
    {
        private int _next;

        public bool Exhausted => _next >= segments.Count;

        public override int Read(Span<byte> destination)
        {
            if (_next >= segments.Count)
            {
                return 0;
            }
            var segment = segments[_next++];
            segment.CopyTo(destination);
            return segment.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Ramp24k(int samples) =>
        Enumerable.Range(0, samples)
            .SelectMany(i =>
            {
                var value = (short)(i * 37 - 4000);
                return new[] { (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF) };
            })
            .ToArray();

    private static OpenAiTextToSpeech Sut(HttpMessageHandler handler, OpenAiTtsConfig? config = null) =>
        new(
            new StubClientFactory(handler),
            config ?? new OpenAiTtsConfig(),
            NullLogger<OpenAiTextToSpeech>.Instance);

    private static HttpResponseMessage PcmResponse(ScriptedStream stream) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

    [Fact]
    public async Task SynthesizeAsync_ChunkedPcmWithOddSplits_YieldsResampledAudioMatchingWholeBuffer()
    {
        var pcm = Ramp24k(400); // 800 bytes
        // Odd split: first segment ends mid-sample; the odd-byte carry must reassemble it.
        var stream = new ScriptedStream([pcm[..301], pcm[301..]]);
        var sut = Sut(new StubHandler(_ => PcmResponse(stream)));

        var collected = new List<byte>();
        var formats = new List<AudioFormat>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
            formats.Add(chunk.Format);
        }

        var expected = new PcmStreamResampler(24000, 22050).Process(pcm);
        collected.ToArray().ShouldBe(expected);
        formats.ShouldAllBe(f => f.SampleRateHz == 22050 && f.SampleWidthBytes == 2 && f.Channels == 1);
    }

    [Fact]
    public async Task SynthesizeAsync_FirstChunk_ArrivesBeforeBodyCompletes()
    {
        var pcm = Ramp24k(400);
        var stream = new ScriptedStream([pcm[..300], pcm[300..600], pcm[600..]]);
        var sut = Sut(new StubHandler(_ => PcmResponse(stream)));

        await using var enumerator = sut
            .SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None)
            .GetAsyncEnumerator();

        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        // Streaming proof: audio was yielded while later segments were still unserved.
        stream.Exhausted.ShouldBeFalse();
    }

    [Fact]
    public async Task SynthesizeAsync_SendsOpenAiSpeechRequest()
    {
        var handler = new StubHandler(_ => PcmResponse(new ScriptedStream([Ramp24k(160)])));
        var sut = Sut(handler, new OpenAiTtsConfig { Voice = "ef_dora", Speed = 1.0 });

        await foreach (var _ in sut.SynthesizeAsync("hola mundo", new SynthesisOptions(), CancellationToken.None))
        {
        }

        handler.LastUri!.ToString().ShouldBe("http://mcp-lemonade:13305/v1/audio/speech");
        var body = JsonNode.Parse(handler.LastBody!)!.AsObject();
        body["model"]!.GetValue<string>().ShouldBe("kokoro-v1");
        body["input"]!.GetValue<string>().ShouldBe("hola mundo");
        body["voice"]!.GetValue<string>().ShouldBe("ef_dora");
        body["speed"]!.GetValue<double>().ShouldBe(1.0);
        body["response_format"]!.GetValue<string>().ShouldBe("pcm");
        body["stream_format"]!.GetValue<string>().ShouldBe("audio");
    }

    [Fact]
    public async Task SynthesizeAsync_OptionsVoice_OverridesConfigVoice()
    {
        var handler = new StubHandler(_ => PcmResponse(new ScriptedStream([Ramp24k(160)])));
        var sut = Sut(handler, new OpenAiTtsConfig { Voice = "ef_dora" });

        await foreach (var _ in sut.SynthesizeAsync(
            "hola", new SynthesisOptions { Voice = "em_alex" }, CancellationToken.None))
        {
        }

        JsonNode.Parse(handler.LastBody!)!["voice"]!.GetValue<string>().ShouldBe("em_alex");
    }

    [Fact]
    public async Task SynthesizeAsync_EmptyPcmBody_ThrowsInsteadOfSilentSuccess()
    {
        var sut = Sut(new StubHandler(_ => PcmResponse(new ScriptedStream([]))));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task SynthesizeAsync_Non2xx_ThrowsBeforeYieldingAudio()
    {
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        }));

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
            {
            }
        });
    }

    [Fact]
    public async Task SynthesizeAsync_MidStreamFailure_SurfacesAfterEarlierChunks()
    {
        var pcm = Ramp24k(400);
        var stream = new ThrowingAfterFirstReadStream(pcm[..400]);
        var sut = Sut(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));

        var yielded = 0;
        await Should.ThrowAsync<IOException>(async () =>
        {
            await foreach (var _ in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
            {
                yielded++;
            }
        });
        yielded.ShouldBe(1);
    }

    private sealed class ThrowingAfterFirstReadStream(byte[] first) : Stream
    {
        private bool _served;

        public override int Read(Span<byte> destination)
        {
            if (_served)
            {
                throw new IOException("connection reset");
            }
            _served = true;
            first.CopyTo(destination);
            return first.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}