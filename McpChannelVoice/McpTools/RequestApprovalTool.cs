using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    private static readonly TimeSpan _captureWindow = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval via voice")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var broker = services.GetRequiredService<ApprovalCaptureBroker>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var session = sessions.Get(conversationId);
        if (session is null)
        {
            return mode == ApprovalMode.Notify ? "notified" : "declined";
        }

        if (mode == ApprovalMode.Notify)
        {
            // Auto-approved tool calls are not narrated over voice — only the agent's
            // spoken content and genuine consent prompts reach the user.
            return "notified";
        }

        var toolList = string.Join(", ", requests.Select(r => r.ToolName.Split("__").Last()));
        var prompt = $"¿Apruebas {toolList}? Di sí o no.";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await SpeakAsync(session, prompt, tts, settings);

            var answer = await broker.WaitForUtteranceAsync(session.SatelliteId, _captureWindow, default);
            var parsed = ApprovalGrammarParser.Parse(answer);

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                SatelliteId = session.SatelliteId,
                Identity = session.Config.Identity,
                Outcome = parsed.ToString(),
                ConversationId = session.ConversationId
            });

            switch (parsed)
            {
                case ApprovalResponse.Approved:
                    return "approved";
                case ApprovalResponse.Declined:
                    return "declined";
            }

            prompt = $"No entendí. ¿Apruebas {toolList}? Di sí o no.";
        }

        return "declined";
    }

    private static async Task SpeakAsync(SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings)
    {
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: tts.SynthesizeAsync(text, options, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
    }
}