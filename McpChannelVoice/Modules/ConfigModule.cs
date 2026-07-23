using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Metrics;
using McpChannelVoice.McpPrompts;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
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

        var emitter = new ChannelNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChannelNotificationEmitter>());

        services
            .AddSingleton(settings)
            .AddSingleton(emitter)
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
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                settings.Stt.OpenAi.AvgLogProbThreshold,
                settings.Stt.OpenAi.NoSpeechProbThreshold,
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
            var inner = new McpChannelVoice.Services.Stt.OpenAiSpeechToText(
                sp.GetRequiredService<IHttpClientFactory>(),
                settings.Stt.OpenAi,
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.OpenAiSpeechToText>>());

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
        services.AddHttpClient();
        services.AddSingleton<InsistentAnnouncementController>();

        services.AddSingleton<Domain.Contracts.IAlertDismisser>(sp => sp.GetRequiredService<ActiveAlertRegistry>());
        services.AddSingleton<Domain.Contracts.ITimerStore, Infrastructure.Timers.InMemoryTimerStore>();
        services.AddSingleton(sp => new Domain.Tools.Timers.Vfs.TimerFileSystem(
            sp.GetRequiredService<Domain.Contracts.ITimerStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Domain.Contracts.IAlertDismisser>()));
        services.AddSingleton<IInsistentAnnouncer>(sp => sp.GetRequiredService<InsistentAnnouncementController>());
        services.AddHostedService<TimerFireService>();

        services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002 // RunSessionHandler is experimental
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    emitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        emitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<CreateConversationTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsReadTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsExecTool>()
            .WithResources<McpResources.FileSystemResource>()
            .WithPrompts<VoiceSystemPrompt>()
            .WithPrompts<TimersSystemPrompt>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
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