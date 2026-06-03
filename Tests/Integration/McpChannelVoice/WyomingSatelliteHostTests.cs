using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class WyomingSatelliteHostTests
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

    private static byte[] Pcm(short value, int bytes = 3200)
    {
        var buf = new byte[bytes];
        for (var i = 0; i + 1 < buf.Length; i += 2)
        {
            buf[i] = (byte)(value & 0xFF);
            buf[i + 1] = (byte)((value >> 8) & 0xFF);
        }
        return buf;
    }

    [Fact]
    public async Task Hub_DialsSatelliteRunsAndStreams_TranscribesAndSendsTranscriptBack()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawRunSatellite = new TaskCompletionSource();
        var sawTranscript = new TaskCompletionSource<string>();

        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            var readLoop = Task.Run(async () =>
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    if (evt.Type == "run-satellite")
                    {
                        sawRunSatellite.TrySetResult();
                    }
                    else if (evt.Type == "transcript")
                    {
                        sawTranscript.TrySetResult(evt.Data["text"]?.GetValue<string>() ?? "");
                    }
                }
            }, ct);

            await sawRunSatellite.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // Wake fired: announce the pipeline, then stream mic audio (no audio-stop).
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }, ct);

        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                         It.IsAny<TranscriptionOptions>(),
                                         It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, opts, token) =>
                {
                    await foreach (var _ in audio.WithCancellation(token))
                    { }
                    return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
                });

        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, new ApprovalCaptureBroker(), manager, 0.4, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new()
            {
                Identity = "household",
                Room = "Kitchen",
                WakeWord = "hey_jarvis",
                Address = $"tcp://127.0.0.1:{port}"
            }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings
            {
                ReconnectDelaySeconds = 1,
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 3000,
                MinSpeechMs = 100
            },
            new VoiceSettings { AgentId = "mycroft" },
            registry, sessions, manager, stt.Object, dispatcher, publisher.Object,
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        var msg = await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        msg.Content.ShouldBe("hola");
        msg.ConversationId.ShouldNotBeNullOrWhiteSpace();
        msg.Sender.ShouldBe("household");
        msg.AgentId.ShouldBe("mycroft");

        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        transcriptText.ShouldBe("hola");

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation / disposal */ }
    }
}