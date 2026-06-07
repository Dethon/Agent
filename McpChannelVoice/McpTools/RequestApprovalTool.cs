using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval via voice")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        var p = new RequestApprovalParams
        {
            ConversationId = conversationId,
            Mode = mode,
            Requests = requests
        };

        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();

        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is null)
        {
            return p.Mode == ApprovalMode.Notify ? "notified" : "rejected";
        }

        if (p.Mode == ApprovalMode.Notify)
        {
            // The tool name itself is never narrated. But if the agent wrote an
            // acknowledgement before this auto-approved tool call, speak it now so the
            // user hears that work is happening while the tool runs (instead of it being
            // buffered with the final answer until the turn completes).
            var pending = accumulator.Flush(p.ConversationId);
            if (!string.IsNullOrWhiteSpace(pending))
            {
                await SpeakAsync(session, pending, tts, settings, AnnouncePriority.Normal);
            }
            return "notified";
        }

        var stt = services.GetRequiredService<ISpeechToText>();
        var wyoming = services.GetRequiredService<WyomingClientSettings>();
        var followUp = settings.FollowUp;

        var toolList = string.Join(", ", p.Requests.Select(r => r.ToolName.Split("__").Last()));
        var prompt = $"¿Apruebas {toolList}? Di sí o no.";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await SpeakAndAwaitAsync(session, prompt, tts, settings, cancellationToken);

            var answer = await CaptureAnswerAsync(session, stt, wyoming, followUp, cancellationToken);
            var parsed = ApprovalGrammarParser.Parse(answer);

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Outcome = parsed.ToString(),
                ConversationId = p.ConversationId
            }, default);

            switch (parsed)
            {
                case ApprovalResponse.Approved:
                    return "approved";
                case ApprovalResponse.Declined:
                    return "rejected";
            }

            prompt = $"No entendí. ¿Apruebas {toolList}? Di sí o no.";
        }

        return "rejected";
    }

    private static async Task SpeakAsync(
        SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings,
        AnnouncePriority priority = AnnouncePriority.High)
    {
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: priority,
            Audio: tts.SynthesizeAsync(text, options, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
    }

    private static async Task SpeakAndAwaitAsync(
        SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings,
        CancellationToken ct)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: tts.SynthesizeAsync(text, new SynthesisOptions { Voice = voice }, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => { drained.TrySetResult(); return Task.CompletedTask; },
            OnDrained: () => { drained.TrySetResult(); return Task.CompletedTask; },
            OnFailed: _ => { drained.TrySetResult(); return Task.CompletedTask; });

        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
        await drained.Task.WaitAsync(ct);
    }

    private static async Task<string> CaptureAnswerAsync(
        SatelliteSession session, ISpeechToText stt, WyomingClientSettings wyoming,
        FollowUpSettings followUp, CancellationToken ct)
    {
        if (followUp.PlaybackTailMs > 0)
        {
            await Task.Delay(followUp.PlaybackTailMs, ct); // echo guard after the prompt finishes
        }

        var capture = session.OpenCapture(new SilenceGate(
            wyoming.SilenceRmsThreshold,
            TimeSpan.FromMilliseconds(wyoming.TrailingSilenceMs),
            TimeSpan.FromMilliseconds(wyoming.MaxUtteranceMs),
            TimeSpan.FromMilliseconds(wyoming.MinSpeechMs),
            noSpeechTimeout: TimeSpan.FromMilliseconds(followUp.WindowMs)));

        var outcome = await capture.Completed.WaitAsync(ct);
        session.CloseCapture();

        if (outcome == CaptureOutcome.NoSpeech)
        {
            return string.Empty;
        }

        var result = await stt.TranscribeAsync(capture.Audio, new TranscriptionOptions(), ct);
        return result.Text ?? string.Empty;
    }
}