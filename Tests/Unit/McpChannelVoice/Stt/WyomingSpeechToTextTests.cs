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
}