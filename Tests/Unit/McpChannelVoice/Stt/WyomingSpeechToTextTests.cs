using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WyomingSpeechToTextTests
{
    [Fact]
    public async Task TranscribeAsync_StreamsAudioAndReturnsTranscript()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var seenChunks = 0;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-chunk")
                {
                    seenChunks++;
                }

                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola mundo",
                            ["language"] = "es"
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        static async IAsyncEnumerable<AudioChunk> audio()
        {
            for (var i = 0; i < 3; i++)
            {
                yield return new AudioChunk
                {
                    Data = new byte[16],
                    Format = AudioFormat.WyomingStandard,
                    Timestamp = TimeSpan.FromMilliseconds(i * 10)
                };
                await Task.Yield();
            }
        }

        var result = await sut.TranscribeAsync(audio(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        seenChunks.ShouldBe(3);
        result.Text.ShouldBe("hola mundo");
        result.Language.ShouldBe("es");
    }

    [Fact]
    public async Task TranscribeAsync_DoesNotSendModelNameOnTranscribeEvent()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        JsonObject? transcribeData = null;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "transcribe")
                {
                    transcribeData = evt.Data;
                }

                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola",
                            ["language"] = "es"
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        static async IAsyncEnumerable<AudioChunk> audio()
        {
            yield return new AudioChunk
            {
                Data = new byte[16],
                Format = AudioFormat.WyomingStandard,
                Timestamp = TimeSpan.Zero
            };
            await Task.Yield();
        }

        await sut.TranscribeAsync(audio(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        transcribeData.ShouldNotBeNull();
        transcribeData!.ContainsKey("name").ShouldBeFalse();
        transcribeData!["language"]?.GetValue<string>().ShouldBe("es");
    }

    [Fact]
    public async Task TranscribeAsync_TranscriptWithStats_ParsesConfidenceAndStats()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola mundo",
                            ["language"] = "es",
                            ["score"] = 0.83,
                            ["avg_logprob"] = -0.19,
                            ["no_speech_prob"] = 0.04,
                            ["compression_ratio"] = 1.2
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        var result = await sut.TranscribeAsync(OneChunk(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        result.Text.ShouldBe("hola mundo");
        result.Confidence.ShouldBe(0.83);
        result.AvgLogProb.ShouldBe(-0.19);
        result.NoSpeechProb.ShouldBe(0.04);
        result.CompressionRatio.ShouldBe(1.2);
    }

    [Fact]
    public async Task TranscribeAsync_TranscriptWithoutStats_FailsOpenWithNulls()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-stop")
                {
                    // Stock (unpatched) server shape: text only.
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject { ["text"] = "hola" }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        var result = await sut.TranscribeAsync(OneChunk(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        result.Text.ShouldBe("hola");
        result.Confidence.ShouldBeNull();
        result.AvgLogProb.ShouldBeNull();
        result.NoSpeechProb.ShouldBeNull();
        result.CompressionRatio.ShouldBeNull();
    }

    [Fact]
    public async Task TranscribeAsync_NonNumericScore_ToleratedAsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola",
                            ["score"] = "high"
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        // Previously GetValue<double>() would throw here and the whole turn would drop as SttError.
        var result = await sut.TranscribeAsync(OneChunk(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        result.Text.ShouldBe("hola");
        result.Confidence.ShouldBeNull();
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        yield return new AudioChunk
        {
            Data = new byte[16],
            Format = AudioFormat.WyomingStandard,
            Timestamp = TimeSpan.Zero
        };
        await Task.Yield();
    }
}