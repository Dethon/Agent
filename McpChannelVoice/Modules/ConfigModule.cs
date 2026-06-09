using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Metrics;
using McpChannelVoice.McpPrompts;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
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
                settings.ConfidenceThreshold,
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

        services.AddSingleton<ISpeechToText>(sp =>
        {
            var inner = new McpChannelVoice.Services.Stt.WyomingSpeechToText(
                settings.Stt.Wyoming,
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.WyomingSpeechToText>>());

            return McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddHostedService<WyomingSatelliteHost>();
        services.AddSingleton(settings.WyomingClient);

        services.AddSingleton<ReplyTextAccumulator>();

        services.AddSingleton<ITextToSpeech>(sp =>
            McpChannelVoice.Services.Tts.SilenceTrimmingTextToSpeech.Wrap(
                new McpChannelVoice.Services.Tts.WyomingTextToSpeech(
                    settings.Tts.Wyoming,
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.WyomingTextToSpeech>>()),
                settings.Tts.Wyoming.TrailingSilenceTrimThreshold));

        services.AddHostedService<WyomingHealthProbeService>();

        services.AddSingleton(settings.Announce);
        services.AddSingleton<AnnouncementService>();

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
            .WithPrompts<VoiceSystemPrompt>()
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