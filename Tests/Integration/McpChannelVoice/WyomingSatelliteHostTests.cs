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
        public override Task EmitMessageNotificationAsync(
            string conversationId, string sender, string content, string? agentId, string? location,
            string? satelliteId, CancellationToken ct = default)
        {
            Tcs.TrySetResult(new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = sender,
                Content = content,
                AgentId = agentId,
                Location = location,
                SatelliteId = satelliteId
            });
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
            emitter, publisher.Object, manager, 0.4, NullLogger<TranscriptDispatcher>.Instance);
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
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = false } },
            registry, sessions, manager, stt.Object, dispatcher, publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        var msg = await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        msg.Content.ShouldBe("hola");
        msg.ConversationId.ShouldNotBeNullOrWhiteSpace();
        msg.Sender.ShouldBe("household");
        msg.AgentId.ShouldBe("mycroft");

        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        transcriptText.ShouldBe(""); // legacy path re-arms with an (ignored) empty transcript

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation / disposal */ }
    }

    [Fact]
    public async Task Hub_FollowUpEnabled_DispatchesFollowUpWithoutSecondWake()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawTranscript = new TaskCompletionSource<string>();
        var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };

        var streamFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);
            var sawRun = new TaskCompletionSource();

            var readLoop = Task.Run(async () =>
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    if (evt.Type == "run-satellite")
                    { sawRun.TrySetResult(); }
                    else if (evt.Type == "transcript")
                    { sawTranscript.TrySetResult(evt.Data["text"]?.GetValue<string>() ?? ""); }
                    // audio-start/audio-chunk/audio-stop (TTS + chime playback) are drained and ignored.
                }
            }, ct);

            await sawRun.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            // First utterance: speech then trailing silence.
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            // Stream the follow-up only once the test confirms the wake-free window is open.
            await streamFollowUp.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

            // Follow-up utterance: more speech then silence — NO new run-pipeline (wake-free).
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }, ct);

        // Two dispatched utterances expected: capture both.
        var dispatched = new List<string>();
        var bothDispatched = new TaskCompletionSource();
        var emitter = new CollectingEmitter(dispatched, bothDispatched, expected: 2);

        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(async (audio, opts, token) =>
            {
                await foreach (var _ in audio.WithCancellation(token))
                { }
                return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
            });

        var publisher = new Mock<IMetricsPublisher>();
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1", "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        var manager = new VoiceConversationManager(factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var dispatcher = new TranscriptDispatcher(emitter, publisher.Object, manager, 0.4, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis", Address = $"tcp://127.0.0.1:{port}" }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings { ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500, TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100 },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 } },
            registry, sessions, manager, stt.Object, dispatcher, publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        // First utterance dispatched -> simulate the agent's spoken reply so the follow-up window opens.
        await WaitForCountAsync(dispatched, 1, TimeSpan.FromSeconds(10));
        sawTranscript.Task.IsCompleted.ShouldBeFalse(); // transcript deferred (no re-arm yet)
        sessions.Get("kitchen-01").ShouldNotBeNull();
        sessions.Get("kitchen-01")!.SignalTurnSpoken();

        // The follow-up window opens asynchronously after the reply signal; wait for the capture to
        // become active, then let the satellite stream the wake-free follow-up utterance into it.
        await WaitForConditionAsync(() => sessions.Get("kitchen-01")?.HasActiveCapture == true, TimeSpan.FromSeconds(10));
        streamFollowUp.TrySetResult();

        // Second utterance must be dispatched WITHOUT a second run-pipeline (wake-free follow-up).
        await bothDispatched.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        dispatched.Count.ShouldBe(2);

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { }
    }

    [Fact]
    public async Task Hub_FollowUpSilence_ReArmsSatelliteWithClosingTranscript()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawTranscript = new TaskCompletionSource<string>();
        var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };

        var streamFollowUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var fakeSatellite = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync(ct);
            await using var stream = conn.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);
            var sawRun = new TaskCompletionSource();

            var readLoop = Task.Run(async () =>
            {
                await foreach (var evt in reader.ReadAllAsync(ct))
                {
                    if (evt.Type == "run-satellite")
                    { sawRun.TrySetResult(); }
                    else if (evt.Type == "transcript")
                    { sawTranscript.TrySetResult(evt.Data["text"]?.GetValue<string>() ?? ""); }
                    // audio-start/audio-chunk/audio-stop (TTS playback) are drained and ignored.
                }
            }, ct);

            await sawRun.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            // First utterance: speech then trailing silence.
            foreach (var _ in Enumerable.Range(0, 4))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(8000)), ct);
            }
            foreach (var _ in Enumerable.Range(0, 6))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }

            // Stream the follow-up only once the test confirms the wake-free window is open.
            await streamFollowUp.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

            // Follow-up window gets ONLY silence: 12 silent chunks (~1.2s) exceed the 800ms no-speech window.
            foreach (var _ in Enumerable.Range(0, 12))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }
            await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }, ct);

        // Only the first utterance should dispatch; the silent follow-up must NOT.
        var dispatched = new List<string>();
        var emitter = new CollectingEmitter(dispatched, new TaskCompletionSource(), expected: 99);

        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(), It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(async (audio, opts, token) =>
            {
                await foreach (var _ in audio.WithCancellation(token))
                { }
                return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
            });

        var publisher = new Mock<IMetricsPublisher>();
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1", "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        var manager = new VoiceConversationManager(factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);
        var dispatcher = new TranscriptDispatcher(emitter, publisher.Object, manager, 0.4, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis", Address = $"tcp://127.0.0.1:{port}" }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings { ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500, TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100 },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 } },
            registry, sessions, manager, stt.Object, dispatcher, publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        // First utterance dispatched -> simulate the agent's spoken reply so the follow-up window opens.
        await WaitForCountAsync(dispatched, 1, TimeSpan.FromSeconds(10));
        sawTranscript.Task.IsCompleted.ShouldBeFalse(); // transcript deferred (no re-arm yet)
        sessions.Get("kitchen-01").ShouldNotBeNull();
        sessions.Get("kitchen-01")!.SignalTurnSpoken();

        // Wait for the wake-free window to open, then stream pure silence into it.
        await WaitForConditionAsync(() => sessions.Get("kitchen-01")?.HasActiveCapture == true, TimeSpan.FromSeconds(10));
        streamFollowUp.TrySetResult();

        // The no-speech timeout fires -> EndConversation writes the closing (empty) transcript to re-arm.
        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        transcriptText.ShouldBe("");
        dispatched.Count.ShouldBe(1); // the silent follow-up was never dispatched

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { }
    }

    private static async Task WaitForCountAsync(List<string> list, int count, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (list.Count < count)
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException($"only {list.Count}/{count}"); }
            await Task.Delay(20);
        }
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
            { throw new TimeoutException("condition not met"); }
            await Task.Delay(20);
        }
    }

    private sealed class CollectingEmitter(List<string> sink, TaskCompletionSource done, int expected)
        : ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance)
    {
        public override Task EmitMessageNotificationAsync(
            string conversationId, string sender, string content, string? agentId, string? location, string? satelliteId, CancellationToken ct = default)
        {
            lock (sink)
            {
                sink.Add(content);
                if (sink.Count >= expected)
                { done.TrySetResult(); }
            }
            return Task.CompletedTask;
        }
    }
}