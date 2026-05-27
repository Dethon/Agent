using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class WyomingServerInboundTests
{
    private sealed class CapturingEmitter : ChannelNotificationEmitter
    {
        public TaskCompletionSource<ChannelMessageNotification> Tcs { get; } = new();
        public CapturingEmitter() : base(NullLogger<ChannelNotificationEmitter>.Instance) { }
        public override Task EmitMessageNotificationAsync(ChannelMessageNotification p, CancellationToken ct = default)
        {
            Tcs.TrySetResult(p);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FakeSatellite_WakesAndStreamsAudio_ProducesTranscript()
    {
        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                          It.IsAny<TranscriptionOptions>(),
                                          It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, _, ct) =>
                {
                    await foreach (var chunk in audio.WithCancellation(ct))
                    {
                        // drain
                    }
                    return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
                });

        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, new ApprovalCaptureBroker(), 0.4, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
        });

        var server = new WyomingServer(
            new WyomingServerSettings { Host = "127.0.0.1", Port = 0 },
            registry, sessions, stt.Object, dispatcher, new ApprovalCaptureBroker(), publisher.Object,
            NullLogger<WyomingServer>.Instance);

        await server.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await using var stream = client.GetStream();
        var writer = new WyomingWriter(stream);

        await writer.WriteAsync(
            WyomingEvent.Header("info", new JsonObject { ["satellite"] = new JsonObject { ["name"] = "kitchen-01" } }),
            CancellationToken.None);

        await writer.WriteAsync(
            WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = 16000, ["width"] = 2, ["channels"] = 1, ["timestamp"] = 0
            }),
            CancellationToken.None);

        await writer.WriteAsync(
            WyomingEvent.WithPayload("audio-chunk",
                new JsonObject { ["rate"] = 16000, ["width"] = 2, ["channels"] = 1, ["timestamp"] = 10 },
                new byte[64]),
            CancellationToken.None);

        await writer.WriteAsync(
            WyomingEvent.Header("audio-stop", new JsonObject { ["timestamp"] = 30 }),
            CancellationToken.None);

        var msg = await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Content.ShouldBe("hola");
        msg.ConversationId.ShouldBe("kitchen-01");
        msg.Sender.ShouldBe("household");

        await server.StopAsync(CancellationToken.None);
    }
}