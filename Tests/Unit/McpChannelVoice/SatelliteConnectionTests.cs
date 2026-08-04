using System.Text.Json.Nodes;
using System.Threading.Channels;
using Channels.Hosting;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Channels.Hosting;

namespace Tests.Unit.McpChannelVoice;

// One run of the link to a satellite, driven by pushing Wyoming events into it and reading back what
// the satellite would have received. The host assembles the connection, so everything under the
// wire is the real thing — the satellite session, both registries, the arbiter, the coordinator, the
// capture session, the silence gate and its factory, the transcript dispatcher and the metric
// publishes. Only the socket is gone.
public class SatelliteConnectionTests
{
    private sealed class Harness
    {
        public WyomingClientSettings Wyoming = new()
        {
            ReconnectDelaySeconds = 1,
            SilenceRmsThreshold = 500,
            TrailingSilenceMs = 200,
            MaxUtteranceMs = 3000,
            MinSpeechMs = 100
        };
        public VoiceSettings Voice = new()
        {
            AgentId = "mycroft",
            FollowUp = new FollowUpSettings { Enabled = false }
        };
        public SatelliteConfig Config = new()
        {
            Identity = "household",
            Room = "Kitchen",
            WakeWord = "hey_jarvis"
        };
        public string TranscriptText = "hola";
        // Runs on the connection's own writer, so a test can park a write the way a dead socket
        // parks one.
        public Func<WyomingEvent, CancellationToken, Task>? WriteHook;

        public readonly ActiveAlertRegistry Alerts = new();
        public readonly SatelliteSessionRegistry Sessions = new();
        public readonly ChannelInboxProbe Emitter = new("voice", DeliveryPolicy.Broadcast);
        public readonly List<VoiceEvent> Published = [];
        public readonly Channel<WyomingEvent> Inbound = Channel.CreateUnbounded<WyomingEvent>();
        public int ChunksTranscribed;
        public WakeArbiter Arbiter = null!;

        private readonly List<WyomingEvent> _written = [];

        public SatelliteConnection Build(string id = "kitchen-01")
        {
            var publisher = new Mock<IMetricsPublisher>();
            publisher.Setup(p => p.Publish(It.IsAny<MetricEvent>()))
                .Callback<MetricEvent>(evt =>
                {
                    if (evt is VoiceEvent voiceEvent)
                    {
                        lock (Published)
                        { Published.Add(voiceEvent); }
                    }
                });

            var factory = new Mock<IConversationFactory>();
            factory.Setup(f => f.CreateAsync(
                    It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var identity = ConversationIdGenerator.CreateFor("topic-x");
                    var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                        $"{Config.Identity} @ {Config.Room}", DateTimeOffset.UtcNow, null);
                    return new ConversationCreation(identity, topic);
                });
            var manager = new VoiceConversationManager(
                factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

            var stt = new Mock<ISpeechToText>();
            stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                             It.IsAny<TranscriptionOptions>(),
                                             It.IsAny<CancellationToken>()))
                .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                    async (audio, _, token) =>
                    {
                        await foreach (var chunk in audio.WithCancellation(token))
                        {
                            Interlocked.Increment(ref ChunksTranscribed);
                        }
                        return new TranscriptionResult
                        {
                            Text = TranscriptText,
                            Language = "es",
                            Confidence = TranscriptText.Length == 0 ? 0.0 : 0.9
                        };
                    });

            var dispatcher = new TranscriptDispatcher(
                Emitter.Emitter, publisher.Object, manager,
                new LocalCommandDispatcher(
                    new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
                -1.0, 0.6, -1.4, 2000, TimeProvider.System, NullLogger<TranscriptDispatcher>.Instance);

            // Arbitration no-ops below two registered handles, so a default instance keeps this
            // construction real without changing what it exercises. Multi-satellite arbitration has
            // its own end-to-end coverage.
            Arbiter = new WakeArbiter(new ArbitrationSettings(), manager, publisher.Object,
                TimeProvider.System, NullLogger<WakeArbiter>.Instance);

            var host = new WyomingSatelliteHost(
                Wyoming, Voice,
                new SatelliteRegistry(new Dictionary<string, SatelliteConfig> { [id] = Config }),
                Sessions, manager, stt.Object, dispatcher, Alerts, publisher.Object,
                TimeProvider.System, Arbiter,
                new SilenceGateFactory(Voice, Wyoming, TimeProvider.System),
                NullLogger<WyomingSatelliteHost>.Instance);

            return host.CreateConnection(id, Config, WriteAsync);
        }

        private async Task WriteAsync(WyomingEvent evt, CancellationToken ct)
        {
            lock (_written)
            { _written.Add(evt); }
            if (WriteHook is { } hook)
            {
                await hook(evt, ct);
            }
        }

        public IAsyncEnumerable<WyomingEvent> Events => Inbound.Reader.ReadAllAsync();

        public void Send(WyomingEvent evt) => Inbound.Writer.TryWrite(evt);

        public void SendWake(JsonObject? data = null) =>
            Send(WyomingEvent.Header("run-pipeline", data ?? []));

        public void SendAudio(short level, int chunks)
        {
            var format = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
            foreach (var _ in Enumerable.Range(0, chunks))
            {
                Send(WyomingEvent.WithPayload("audio-chunk", format.DeepClone().AsObject(), Pcm(level)));
            }
        }

        public void DropLink() => Inbound.Writer.TryComplete();

        public IReadOnlyList<WyomingEvent> WrittenOfType(string type)
        {
            lock (_written)
            { return _written.Where(e => e.Type == type).ToList(); }
        }

        public VoiceEvent[] PublishedOf(VoiceMetric metric)
        {
            lock (Published)
            { return Published.Where(e => e.Metric == metric).ToArray(); }
        }
    }

    // Constant-amplitude S16LE. 3200 bytes = 100 ms at 16 kHz mono.
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

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met");
            }
            await Task.Delay(20);
        }
    }

    private static async Task StopAsync(SatelliteConnection connection, Task run, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        try
        { await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* the run throws or cancels as the link ends */ }
    }

    [Fact]
    public async Task Wake_CommandRunsOnFromTheWakeWord_UsesTheRoomLevelTheSatelliteMeasured()
    {
        // Field report 2026-07-30: "sometimes the voice starts processing when I'm still talking."
        // With no gap after the wake word, the capture's first frames ARE the command, so the
        // gate's noise floor froze at the speaker's own level (6x the room, measured on prod) and
        // the rest of the utterance read as background. The satellite listens to the room the
        // whole time it is idle, so it can report what silence there actually sounds like; the hub
        // caps the floor with it and the command survives.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness
        {
            Wyoming = new WyomingClientSettings
            {
                ReconnectDelaySeconds = 1,
                SilenceRmsThreshold = 500,
                TrailingSilenceMs = 200,
                MaxUtteranceMs = 10_000,
                MinSpeechMs = 100
            },
            Voice = new VoiceSettings { AgentId = "nabu", FollowUp = new FollowUpSettings { Enabled = false } },
            Config = new SatelliteConfig { Identity = "household", Room = "Office", WakeWord = "ok_nabu" },
            TranscriptText = "baja el volumen al diez por ciento"
        };
        var connection = h.Build("office-01");
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake(new JsonObject
        {
            ["source"] = "wake",
            ["wake_rms"] = 9000.0,
            // Measured on the satellite while idle: no wake word, no capture, just the room.
            ["room_rms"] = 60.0
        });
        h.SendAudio(8000, 4);  // the command starts on the very first frame — no pre-roll gap
        h.SendAudio(2000, 8);  // a quieter clause: 12 dB under the peak, still far above the clamp
        h.SendAudio(0, 20);    // the user actually stops

        var msg = await h.Emitter.FirstAsync(TimeSpan.FromSeconds(10), cts.Token);
        msg.Content.ShouldBe("baja el volumen al diez por ciento");
        // The whole command reached STT: 12 spoken chunks plus the trailing run that ended it. An
        // uncapped floor endpoints inside the quieter clause (or drops the turn as no-speech).
        h.ChunksTranscribed.ShouldBeGreaterThanOrEqualTo(12);

        await StopAsync(connection, run, cts);
    }

    // Only run-pipeline carries the wake metadata; a satellite that also announces its mic stream
    // sends a metadata-free audio-start for the SAME turn. WakeTriggered must report exactly what
    // opened the turn — that field is what RmsOffsetDb is calibrated from, and a wrong value is
    // worse than a missing one because nothing ever fails. Both frame orders are exercised: the
    // canonical one must keep the signal, and the reversed one must not attribute a signal that
    // arrived afterwards to a turn that was already open.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Wake_MetadataOnRunPipelineWithAudioStart_AttributesSignalToTheTurnThatOpened(
        bool runPipelineFirst)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness();
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        var format = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };
        var runPipeline = WyomingEvent.Header("run-pipeline", new JsonObject
        {
            ["source"] = "wake",
            ["wake_rms"] = 1234.5,
            ["wake_score"] = 0.87
        });
        // The other announcement of the same turn: a mic-stream open, carrying no wake metadata.
        var audioStart = WyomingEvent.Header("audio-start", format.DeepClone().AsObject());

        h.Send(runPipelineFirst ? runPipeline : audioStart);
        h.Send(runPipelineFirst ? audioStart : runPipeline);

        // Pre-roll gap: real captures open on ambient/gap frames from wake-detection latency,
        // seeding the AdaptiveLevelTracker's noise floor before real speech classifies as speech.
        h.SendAudio(0, 1);
        h.SendAudio(8000, 4);
        h.SendAudio(0, 6);

        await h.Emitter.FirstAsync(TimeSpan.FromSeconds(10), cts.Token);
        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(10));

        var wakes = h.PublishedOf(VoiceMetric.WakeTriggered);
        // One wake for one turn — the second frame must not open another.
        wakes.Length.ShouldBe(1);
        // Canonical order: the announcement reached the opening, so it is reported. Reversed:
        // audio-start already opened the turn with no announcement, and the coordinator's early
        // return discarded the one that arrived afterwards — "unknown" is the only honest answer.
        wakes[0].WakeRms.ShouldBe(runPipelineFirst ? 1234.5 : null);
        wakes[0].WakeScore.ShouldBe(runPipelineFirst ? 0.87 : null);

        await StopAsync(connection, run, cts);
    }

    [Fact]
    public async Task Wake_ThenSilence_ReArmsWithoutWaitingForMaxUtterance()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness
        {
            Voice = new VoiceSettings
            {
                AgentId = "mycroft",
                FollowUp = new FollowUpSettings
                { Enabled = true, Chime = false, PlaybackTailMs = 0, WindowMs = 800 }
            }
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        h.SendWake();
        // Wake fired but the user says nothing: stream ONLY silence. ~1.2s (12 chunks) exceeds the
        // 800ms no-speech window yet stays well under the 3000ms max-utterance cap.
        h.SendAudio(0, 12);

        // The no-speech window must fire on the wake turn too: re-arm with the closing (empty)
        // transcript instead of holding the mic open until the max-utterance cap, and never dispatch.
        await UntilAsync(() => h.WrittenOfType("transcript").Count > 0, TimeSpan.FromSeconds(8));
        h.WrittenOfType("transcript")[0].Data["text"]!.GetValue<string>().ShouldBe("");
        h.Emitter.Received().Count.ShouldBe(0);

        await StopAsync(connection, run, cts);
    }

    [Fact]
    public async Task Wake_WithoutUtterance_AcknowledgesActiveAlertOnThatSatellite()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // STT returns empty so nothing would be dispatched — the ack must come from the wake, not
        // from a dispatch.
        var h = new Harness { TranscriptText = "" };
        using var alertCts = new CancellationTokenSource();
        h.Alerts.Register(new AlertHandle(alertCts, ["kitchen-01"], "test alert", AnnounceKind.Alarm));
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        // Wake fired but no audio data (the user stays silent). A bare wake alone must be enough to
        // dismiss the active alert.
        h.SendWake();

        await UntilAsync(() => alertCts.IsCancellationRequested, TimeSpan.FromSeconds(5));
        alertCts.IsCancellationRequested.ShouldBeTrue();

        await StopAsync(connection, run, cts);
    }

    // The rule the unwind split exists to carry. A satellite whose link just died must stop being an
    // arbitration candidate before anything unbounded runs, or it can still win a wake against a
    // live satellite and silently suppress a real command. The playback loop is exactly that
    // unbounded thing — it can be parked writing to the socket that just died.
    [Fact]
    public async Task Unwind_PlaybackStillDraining_ArbiterRegistrationIsAlreadyReleased()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Harness();
        h.WriteHook = async (evt, _) =>
        {
            if (evt.Type != "audio-chunk")
            {
                return;
            }
            writeStarted.TrySetResult();
            await parked.Task;
        };
        var connection = h.Build();
        var run = connection.RunAsync(h.Events, cts.Token);

        await UntilAsync(() => h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        await connection.Session.EnqueuePlaybackAsync(
            new PlaybackJob(
                Label: "reply",
                Priority: AnnouncePriority.Normal,
                Audio: OneChunk(),
                OnStarted: _ => Task.CompletedTask,
                OnPreempted: _ => Task.CompletedTask),
            queueMaxDepth: 8);
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cts.Token);

        h.DropLink();

        // The link is gone and the playback write is still parked, so the drain cannot have got past
        // it. The registration is already released anyway, because releasing it is not in the drain.
        await UntilAsync(() => !h.Arbiter.IsRegistered("kitchen-01"), TimeSpan.FromSeconds(5));
        run.IsCompleted.ShouldBeFalse();

        parked.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        // And the rest of the unwind still ran once the drain could finish.
        h.Sessions.Get("kitchen-01").ShouldBeNull();
        connection.Session.ControlWriter.ShouldBeNull();
    }

    // The host's reconnect loop is built on the run throwing: a link that dies has to surface as an
    // exception here, not as a quiet return that would leave the loop believing the satellite is
    // still connected.
    [Fact]
    public async Task Run_InboundStreamFaults_ThrowsSoTheReconnectLoopRetries()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var h = new Harness();
        var connection = h.Build();

        var run = connection.RunAsync(Failing(), cts.Token);

        await Should.ThrowAsync<IOException>(() => run);
        // Unwound on the way out, even though the read loop left by an exception.
        h.Sessions.Get("kitchen-01").ShouldBeNull();
        h.Arbiter.IsRegistered("kitchen-01").ShouldBeFalse();
    }

    private static async IAsyncEnumerable<WyomingEvent> Failing()
    {
        await Task.Yield();
        throw new IOException("satellite connection dropped");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<AudioChunk> OneChunk()
    {
        await Task.Yield();
        yield return new AudioChunk
        {
            Data = Pcm(1000),
            Format = new AudioFormat { SampleRateHz = 22_050, SampleWidthBytes = 2, Channels = 1 }
        };
    }
}