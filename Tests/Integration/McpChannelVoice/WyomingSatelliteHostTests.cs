using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
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
            string? satelliteId, string? dismissedAlert, CancellationToken ct = default)
        {
            Tcs.TrySetResult(new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = sender,
                Content = content,
                AgentId = agentId,
                Location = location,
                SatelliteId = satelliteId,
                DismissedAlert = dismissedAlert
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

    private sealed class RejectingVerifier : global::McpChannelVoice.Services.Verification.ISpeakerVerifier
    {
        public Task<global::McpChannelVoice.Services.Verification.SpeakerVerification> VerifyAsync(
            IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
            bool enforceMinSpeech = true) =>
            Task.FromResult(new global::McpChannelVoice.Services.Verification.SpeakerVerification(
                global::McpChannelVoice.Services.Verification.SpeakerDecision.Rejected, 0.12, null));
    }

    private sealed class IdentifyingVerifier(string name)
        : global::McpChannelVoice.Services.Verification.ISpeakerVerifier
    {
        public Task<global::McpChannelVoice.Services.Verification.SpeakerVerification> VerifyAsync(
            IReadOnlyList<AudioChunk> speechAudio, long speechMs, SatelliteConfig config, CancellationToken ct,
            bool enforceMinSpeech = true) =>
            Task.FromResult(new global::McpChannelVoice.Services.Verification.SpeakerVerification(
                global::McpChannelVoice.Services.Verification.SpeakerDecision.Accepted, 0.91, name, name));
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
            // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
            // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
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
        string? capturedLanguage = null;
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                         It.IsAny<TranscriptionOptions>(),
                                         It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, opts, token) =>
                {
                    capturedLanguage = opts.Language;
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
            emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new()
            {
                Identity = "household",
                Room = "Kitchen",
                WakeWord = "hey_jarvis",
                Address = $"tcp://127.0.0.1:{port}",
                // Per-satellite STT language override must reach the backend (symmetric with the
                // per-satellite Tts.Wyoming.Voice override), not be silently dropped.
                Stt = new SttSettings { Wyoming = new WyomingSttConfig { Language = "en" } }
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
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        var msg = await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        msg.Content.ShouldBe("hola");
        msg.ConversationId.ShouldNotBeNullOrWhiteSpace();
        msg.Sender.ShouldBe("household");
        msg.AgentId.ShouldBe("mycroft");

        capturedLanguage.ShouldBe("en"); // per-satellite Stt.Wyoming.Language threaded into TranscriptionOptions

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
    public async Task Hub_ConclusiveSpeaker_EmitsIdentifiedPersonAsSender()
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
                    { sawRunSatellite.TrySetResult(); }
                    else if (evt.Type == "transcript")
                    { sawTranscript.TrySetResult(evt.Data["text"]?.GetValue<string>() ?? ""); }
                }
            }, ct);

            await sawRunSatellite.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
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
        TranscriptionOptions? capturedOptions = null;
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                         It.IsAny<TranscriptionOptions>(),
                                         It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, opts, token) =>
                {
                    capturedOptions = opts;
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
            emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
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
            // EarlyVerifyMs = 0 keeps the fake off the early-close path; only the terminal verify
            // (which yields the identity) drives this test.
            new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings { Enabled = false },
                SpeakerVerification = new SpeakerVerificationSettings { EarlyVerifyMs = 0 }
            },
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance,
            new IdentifyingVerifier("fran"));

        await host.StartAsync(ct);

        var msg = await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        msg.Content.ShouldBe("hola");
        msg.Sender.ShouldBe("fran"); // conclusive identity routed into Sender, not "household"

        // The gate's verdict must reach the STT chain, not just the message Sender: the decorator's
        // whole TSE policy is keyed off TargetSpeaker, so prove the wiring lands the identified name
        // there (not merely "the floor is positive", which would pin clamping instead of wiring).
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.TargetSpeaker.ShouldBe("fran");

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation / disposal */ }
    }

    [Fact]
    public async Task Hub_UnknownSpeaker_RejectsCaptureWithoutSttAndPublishesMetric()
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
            // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
            // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            // Loud portion extended to 10 chunks (~1000ms) so capture.Stats.SpeechMs clears the
            // verifier's default 800ms MinVerifySpeechMs — the template test's 4 chunks (~400ms)
            // would short-circuit verification to Skipped instead of exercising Rejected.
            foreach (var _ in Enumerable.Range(0, 10))
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
        var publishedEvents = new List<VoiceEvent>();
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((evt, _) =>
            {
                if (evt is VoiceEvent voiceEvent)
                {
                    lock (publishedEvents)
                    { publishedEvents.Add(voiceEvent); }
                }
            })
            .Returns(Task.CompletedTask);
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
            emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
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
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance,
            new RejectingVerifier());

        await host.StartAsync(ct);

        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        transcriptText.ShouldBe(""); // closing transcript re-arms the satellite; conversation ended without STT

        stt.Verify(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                          It.IsAny<TranscriptionOptions>(), It.IsAny<CancellationToken>()), Times.Never());

        var rejection = publishedEvents.SingleOrDefault(e => e.Metric == VoiceMetric.UtteranceRejected);
        rejection.ShouldNotBeNull();
        rejection!.Outcome.ShouldBe("unknown_speaker");
        rejection.Similarity.ShouldBe(0.12);

        emitter.Tcs.Task.IsCompleted.ShouldBeFalse(); // no message notification reached the agent

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

            // First utterance: speech then trailing silence. Leading silent chunk seeds the
            // AdaptiveLevelTracker's noise floor (models the real pre-roll gap).
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
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

            // Follow-up utterance: more speech then silence — NO new run-pipeline (wake-free). A fresh
            // SilenceGate+AdaptiveLevelTracker is opened per capture, so this also needs its own
            // leading silent chunk to seed the floor.
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
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
        var dispatcher = new TranscriptDispatcher(emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis", Address = $"tcp://127.0.0.1:{port}" }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings { ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500, TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100 },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 } },
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance);

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

            // First utterance: speech then trailing silence. Leading silent chunk seeds the
            // AdaptiveLevelTracker's noise floor (models the real pre-roll gap).
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
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
        var dispatcher = new TranscriptDispatcher(emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis", Address = $"tcp://127.0.0.1:{port}" }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings { ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500, TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100 },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 } },
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance);

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

    [Fact]
    public async Task Hub_WakeThenSilence_ReArmsWithoutWaitingForMaxUtterance()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawTranscript = new TaskCompletionSource<string>();
        var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };

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
                }
            }, ct);

            await sawRun.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);

            // Wake fired but the user says nothing: stream ONLY silence. ~1.2s (12 chunks) exceeds the
            // 800ms no-speech window yet stays well under the 3000ms max-utterance cap.
            foreach (var _ in Enumerable.Range(0, 12))
            {
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
            }
            await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        }, ct);

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
        var dispatcher = new TranscriptDispatcher(emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis", Address = $"tcp://127.0.0.1:{port}" }
        });

        var host = new WyomingSatelliteHost(
            new WyomingClientSettings { ReconnectDelaySeconds = 1, SilenceRmsThreshold = 500, TrailingSilenceMs = 200, MaxUtteranceMs = 3000, MinSpeechMs = 100 },
            new VoiceSettings { AgentId = "mycroft", FollowUp = new FollowUpSettings { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 } },
            registry, sessions, manager, stt.Object, dispatcher, new ActiveAlertRegistry(), publisher.Object, TimeProvider.System, NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        // The no-speech window must fire on the wake turn too: re-arm with the closing (empty)
        // transcript instead of holding the mic open until the max-utterance cap, and never dispatch.
        var transcriptText = await sawTranscript.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
        transcriptText.ShouldBe("");
        dispatched.Count.ShouldBe(0);

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { }
    }

    [Fact]
    public async Task Hub_DispatchedUtterance_AcknowledgesActiveAlertOnThatSatellite()
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
            // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
            // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
            await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data.DeepClone().AsObject(), Pcm(0)), ct);
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
            emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
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

        var alerts = new ActiveAlertRegistry();
        using var alertCts = new CancellationTokenSource();
        alerts.Register(new AlertHandle(alertCts, ["kitchen-01"], "test alert", AnnounceKind.Alarm));

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
            registry, sessions, manager, stt.Object, dispatcher, alerts, publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct); // utterance dispatched
        await WaitForConditionAsync(() => alertCts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        alertCts.IsCancellationRequested.ShouldBeTrue(); // the alert was acknowledged

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation */ }
    }

    [Fact]
    public async Task Hub_WakeWithoutUtterance_AcknowledgesActiveAlertOnThatSatellite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = cts.Token;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var sawRunSatellite = new TaskCompletionSource();
        var wakeSent = new TaskCompletionSource();

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
                    { sawRunSatellite.TrySetResult(); }
                    // audio-start/chunk/stop, transcript, etc. are drained and ignored.
                }
            }, ct);

            await sawRunSatellite.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // Wake fired: send run-pipeline but NO audio data (user stays silent).
            // Bare wake alone must be enough to dismiss the active alert.
            await writer.WriteAsync(WyomingEvent.Header("run-pipeline", new JsonObject()), ct);
            wakeSent.TrySetResult();

            // Keep the connection open so the hub can process the event.
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }, ct);

        // STT returns empty so nothing would be dispatched — ack must come from wake, not dispatch.
        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                         It.IsAny<TranscriptionOptions>(),
                                         It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, opts, token) =>
                {
                    await foreach (var _ in audio.WithCancellation(token))
                    { }
                    return new TranscriptionResult { Text = "", Language = "es", Confidence = 0.0 };
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
            emitter, publisher.Object, manager, 0.4, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);
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

        var alerts = new ActiveAlertRegistry();
        using var alertCts = new CancellationTokenSource();
        alerts.Register(new AlertHandle(alertCts, ["kitchen-01"], "test alert", AnnounceKind.Alarm));

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
            registry, sessions, manager, stt.Object, dispatcher, alerts, publisher.Object,
            TimeProvider.System,
            NullLogger<WyomingSatelliteHost>.Instance);

        await host.StartAsync(ct);

        // Wait until the fake satellite has sent the wake event, then assert that the
        // alert token is cancelled — proving the bare wake dismissed it with no transcript.
        await wakeSent.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await WaitForConditionAsync(() => alertCts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        alertCts.IsCancellationRequested.ShouldBeTrue(); // bare wake word dismissed the alert

        await host.StopAsync(CancellationToken.None);
        listener.Stop();
        await cts.CancelAsync();
        try
        { await fakeSatellite; }
        catch { /* cancellation */ }
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
            string conversationId, string sender, string content, string? agentId, string? location, string? satelliteId, string? dismissedAlert, CancellationToken ct = default)
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