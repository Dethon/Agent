using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class WyomingTextToSpeechTests
{
    [Fact]
    public async Task SynthesizeAsync_StreamsChunksBack()
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
                if (evt.Type != "synthesize")
                {
                    continue;
                }

                await writer.WriteAsync(WyomingEvent.Header("audio-start",
                    new JsonObject { ["rate"] = 22050, ["width"] = 2, ["channels"] = 1 }),
                    CancellationToken.None);
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk",
                    new JsonObject { ["rate"] = 22050, ["width"] = 2, ["channels"] = 1 },
                    new byte[] { 1, 2, 3, 4 }),
                    CancellationToken.None);
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk",
                    new JsonObject { ["rate"] = 22050, ["width"] = 2, ["channels"] = 1 },
                    new byte[] { 5, 6, 7, 8 }),
                    CancellationToken.None);
                await writer.WriteAsync(WyomingEvent.Header("audio-stop", new JsonObject()),
                    CancellationToken.None);
                return;
            }
        });

        var sut = new WyomingTextToSpeech(
            new WyomingTtsConfig { Host = "127.0.0.1", Port = port, Voice = "es_ES-davefx-medium" },
            NullLogger<WyomingTextToSpeech>.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
        }
        await serverTask;
        listener.Stop();

        collected.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
    }
}