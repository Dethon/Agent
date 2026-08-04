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
        var gates = services.GetRequiredService<SilenceGateFactory>();
        var time = services.GetRequiredService<TimeProvider>();

        var toolList = string.Join(", ", p.Requests.Select(r => r.ToolName.Split("__").Last()));
        var prompt = $"¿Apruebas {toolList}? Di sí o no.";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (!await SpeakAndAwaitAsync(session, prompt, tts, settings, cancellationToken))
            {
                // Satellite disconnected mid-approval; abandon rather than opening a capture on a
                // dead session that would block until the request is cancelled.
                return "rejected";
            }

            var answer = await CaptureAnswerAsync(session, stt, gates, settings, time, cancellationToken);
            if (answer is null)
            {
                // Arbitration stole the turn mid-answer: the arbiter already re-armed this
                // satellite via pause-satellite, so there is no one left here to re-prompt.
                return "rejected";
            }
            var parsed = ApprovalGrammarParser.Parse(answer);

            metrics.Publish(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Outcome = parsed.ToString(),
                ConversationId = p.ConversationId
            });

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
        var options = new SynthesisOptions { Voice = session.ResolveVoice(settings) };
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: priority,
            Audio: tts.SynthesizeAsync(text, options, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
    }

    private static async Task<bool> SpeakAndAwaitAsync(
        SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings,
        CancellationToken ct)
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: tts.SynthesizeAsync(
                text, new SynthesisOptions { Voice = session.ResolveVoice(settings) }, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => { drained.TrySetResult(); return Task.CompletedTask; },
            OnDrained: () => { drained.TrySetResult(); return Task.CompletedTask; },
            OnFailed: _ => { drained.TrySetResult(); return Task.CompletedTask; });

        var accepted = await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
        if (!accepted)
        {
            // Satellite disconnected between session resolution and enqueue (playback channel
            // completed) — signal the caller to abandon the approval instead of opening a capture
            // on a dead session that would block until the request is cancelled.
            return false;
        }
        await drained.Task.WaitAsync(ct);
        return true;
    }

    // Returns null when arbitration abandoned the capture — distinct from an empty answer,
    // which re-prompts.
    private static async Task<string?> CaptureAnswerAsync(
        SatelliteSession session, ISpeechToText stt, SilenceGateFactory gates,
        VoiceSettings settings, TimeProvider time, CancellationToken ct)
    {
        var followUp = settings.FollowUp;
        if (followUp.PlaybackTailMs > 0)
        {
            await Task.Delay(followUp.PlaybackTailMs, ct); // echo guard after the prompt finishes
        }

        // The same gate the wake turn the user is answering was endpointed against, room-noise cap
        // included: a confirmation mic that behaved differently from the mic that heard the question
        // cut people off mid-answer.
        var capture = session.OpenCapture(
            gates.Create(session.SatelliteId, session.Config),
            // The approval mic is an open capture like any wake turn's: Rule B must be able
            // to ask it what it heard during another satellite's wake-word span.
            new ChunkHistory(time, settings.Arbitration.HistorySpan));

        CaptureOutcome outcome;
        try
        {
            outcome = await capture.Completed.WaitAsync(ct);
        }
        finally
        {
            // Always close the capture, even if the wait is cancelled, so a cancelled approval
            // doesn't leave a dangling mic capture routing audio into a dead turn.
            session.CloseCapture();
            // And pay back into the memory this capture's gate read from; the factory decides
            // whether it measured anything worth keeping.
            gates.RecordCaptureClose(session.SatelliteId, capture.Stats);
        }

        if (outcome == CaptureOutcome.Abandoned)
        {
            return null;
        }

        if (outcome == CaptureOutcome.NoSpeech)
        {
            return string.Empty;
        }

        var result = await stt.TranscribeAsync(capture.Audio, new TranscriptionOptions(), ct);
        return result.Text ?? string.Empty;
    }
}