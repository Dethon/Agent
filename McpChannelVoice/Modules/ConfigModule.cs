using Domain.Agents;
using Domain.Channels;
using Domain.Contracts;
using Infrastructure.Metrics;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.Verification;
using McpChannelVoice.Settings;
using ModelContextProtocol.Protocol;
using StackExchange.Redis;

namespace McpChannelVoice.Modules;

public static class ConfigModule
{
    public static VoiceSettings GetVoiceSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<VoiceSettings>()
                       ?? throw new InvalidOperationException("Voice settings not found");
        return settings.WithResolvedLocalityDefaults();
    }

    public static IServiceCollection ConfigureVoiceChannel(
        this IServiceCollection services,
        VoiceSettings settings)
    {
        var redisConnection = settings.RedisConnectionString;

        services
            .AddSingleton(settings)
            .AddSingleton<ChannelInbox>()
            .AddSingleton<ChannelNotificationEmitter>()
            .AddSingleton(new SatelliteRegistry(settings.Satellites))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection))
            .AddSingleton<IMetricsPublisher, RedisMetricsPublisher>()
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton(TimeProvider.System)
            .AddSingleton<Domain.Contracts.IThreadStateStore>(sp =>
                new Infrastructure.StateManagers.RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromDays(30)))
            .AddSingleton<Domain.Contracts.IConversationFactory, Infrastructure.Conversations.ConversationFactory>()
            .AddHostedService(sp =>
                new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "mcp-channel-voice"));

        services
            .AddSingleton<SatelliteSessionRegistry>()
            .AddSingleton(new VoiceCommandMatcher(settings.Commands))
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                sp.GetRequiredService<VoiceCommandMatcher>(),
                avgLogProbThreshold: settings.Stt.OpenAi.AvgLogProbThreshold,
                noSpeechProbThreshold: settings.Stt.OpenAi.NoSpeechProbThreshold,
                shortSpeechAvgLogProbThreshold: settings.Stt.OpenAi.ShortSpeechAvgLogProbThreshold,
                fullThresholdSpeechMs: settings.Stt.OpenAi.FullThresholdSpeechMs,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
            .AddSingleton(sp => new VoiceConversationManager(
                sp.GetRequiredService<Domain.Contracts.IConversationFactory>(),
                sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<TimeProvider>(),
                settings.ConversationLifetime,
                sp.GetRequiredService<ILogger<VoiceConversationManager>>()))
            .AddSingleton(sp => new VoiceDeliveryRegistry(
                sp.GetRequiredService<TimeProvider>(),
                settings.ConversationLifetime,
                sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<ILogger<VoiceDeliveryRegistry>>()));

        // Streaming TTS reads can outlive the default 100 s client timeout on long replies;
        // cancellation is driven by the per-turn CancellationToken instead (STT self-bounds via
        // RequestTimeout).
        services.AddHttpClient(LemonadeHttp.ClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);

        services.AddSingleton<Services.Tse.ITseExtractorClient>(sp =>
            new Services.Tse.TseExtractorClient(
                // No HttpClient.Timeout: the client arms its own deadline from Tse.TimeoutMs via a
                // linked token, so the framework's 100s default must not silently cap it — an owner
                // raising TimeoutMs above 100s would otherwise get a misreported sidecar failure.
                new HttpClient { Timeout = Timeout.InfiniteTimeSpan },
                settings.Tse,
                sp.GetRequiredService<ILogger<Services.Tse.TseExtractorClient>>()));
        services.AddSingleton(sp => new Services.Tse.TseAuditTrail(
            settings.Tse.AuditDir,
            settings.Tse.AuditMaxPairs,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<Services.Tse.TseAuditTrail>>()));

        services.AddSingleton<ISpeechToText>(sp =>
        {
            var sttLogger = sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.OpenAiSpeechToText>>();
            var overBudget = McpChannelVoice.Services.Stt.WhisperPromptBuilder.OverBudgetPromptSources(settings);
            if (overBudget.Count > 0)
            {
                sttLogger.LogWarning(
                    "Whisper prompt template(s) longer than MaxPromptChars={MaxChars} are posted whole, "
                    + "and whisper.cpp truncates keeping the tail — the front of the vocabulary is lost: {Sources}",
                    settings.Stt.OpenAi.MaxPromptChars, string.Join(", ", overBudget));
            }

            var inner = new McpChannelVoice.Services.Stt.OpenAiSpeechToText(
                sp.GetRequiredService<IHttpClientFactory>(),
                settings.Stt.OpenAi,
                sttLogger);

            var segmented = McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, settings.WyomingClient, sp.GetRequiredService<ILoggerFactory>());
            return Services.Tse.TseSpeechToText.Wrap(
                segmented,
                settings.Tse,
                sp.GetRequiredService<Services.Tse.ITseExtractorClient>(),
                sp.GetRequiredService<Services.Tse.TseAuditTrail>(),
                sp.GetRequiredService<Domain.Contracts.IMetricsPublisher>(),
                sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddSingleton<ISpeakerVerifier>(sp =>
            new SpeakerVerifier(
                settings.SpeakerVerification,
                () =>
                {
                    var embedder = new OnnxSpeakerEmbedder(settings.SpeakerVerification.ModelPath);
                    var profiles = new SpeakerProfileStore(
                        settings.SpeakerVerification.VoicesPath,
                        embedder,
                        sp.GetRequiredService<ILogger<SpeakerProfileStore>>()).Load();
                    return (embedder, profiles);
                },
                sp.GetRequiredService<ILogger<SpeakerVerifier>>()));

        services.AddHostedService<WyomingSatelliteHost>();
        services.AddSingleton(settings.WyomingClient);
        services.AddSingleton(settings.Arbitration);

        services.AddSingleton<ReplyTextAccumulator>();

        services.AddSingleton<ITextToSpeech>(sp =>
            McpChannelVoice.Services.Tts.SilenceTrimmingTextToSpeech.Wrap(
                new McpChannelVoice.Services.Tts.OpenAiTextToSpeech(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    settings.Tts.OpenAi,
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.OpenAiTextToSpeech>>()),
                settings.Tts.OpenAi.TrailingSilenceTrimThreshold));

        services.AddSingleton(settings.Announce);
        services.AddSingleton<AnnouncementService>();
        services.AddSingleton<ActiveAlertRegistry>();
        services.AddSingleton<WakeArbiter>();
        services.AddHttpClient();
        services.AddSingleton<InsistentAnnouncementController>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<CreateConversationTool>()
            .WithTools<McpChannelReceiveTool>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // channel_receive's long poll ends in cancellation whenever the agent hangs up
                    // or the server shuts down. Mapping that to IsError would hand the pump an
                    // error result to retry on; let it propagate as the abort it is.
                    throw;
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }
            }));

        return services;
    }
}