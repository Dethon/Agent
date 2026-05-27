using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Metrics;
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

        var settings = config.GetSection("Voice").Get<VoiceSettings>()
                       ?? throw new InvalidOperationException("Voice settings not found");
        return settings;
    }

    public static IServiceCollection ConfigureVoiceChannel(
        this IServiceCollection services,
        IConfiguration configuration,
        VoiceSettings settings)
    {
        var redisConnection = configuration.GetSection("Redis")["ConnectionString"]
                              ?? "redis:6379";

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
            .AddHostedService(sp =>
                new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "mcp-channel-voice"));

        services
            .AddSingleton<SatelliteSessionRegistry>()
            .AddSingleton<ApprovalCaptureBroker>()
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<ApprovalCaptureBroker>(),
                settings.ConfidenceThreshold,
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()));

        if (settings.Stt.Provider.Equals("Wyoming", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISpeechToText>(sp => new McpChannelVoice.Services.Stt.WyomingSpeechToText(
                settings.Stt.Wyoming ?? throw new InvalidOperationException("Stt.Wyoming missing"),
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.WyomingSpeechToText>>()));
        }

        services.AddHostedService<WyomingServer>();
        services.AddSingleton(settings.WyomingServer);

        services.AddSingleton<ReplyTextAccumulator>();

        if (settings.Tts.Provider.Equals("Wyoming", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITextToSpeech>(sp => new McpChannelVoice.Services.Tts.WyomingTextToSpeech(
                settings.Tts.Wyoming ?? throw new InvalidOperationException("Tts.Wyoming missing"),
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.WyomingTextToSpeech>>()));
        }

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