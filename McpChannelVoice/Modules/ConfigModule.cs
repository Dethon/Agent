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

        var settings = config.Get<VoiceSettings>()
                       ?? throw new InvalidOperationException("Voice settings not found");
        return settings;
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
            .AddSingleton<ApprovalCaptureBroker>()
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<ApprovalCaptureBroker>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                settings.ConfidenceThreshold,
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
            .AddSingleton(sp => new VoiceConversationManager(
                sp.GetRequiredService<Domain.Contracts.IConversationFactory>(),
                sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<TimeProvider>(),
                settings.ConversationLifetime,
                sp.GetRequiredService<ILogger<VoiceConversationManager>>()));

        services.AddHttpClient("openai", c => c.BaseAddress = new Uri("https://api.openai.com"));
        services.AddHttpClient("openrouter", c => c.BaseAddress = new Uri("https://openrouter.ai"));

        services.AddSingleton<ISpeechToText>(sp =>
        {
            ISpeechToText inner;
            if (settings.Stt.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? throw new InvalidOperationException("OPENAI_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
                inner = new Infrastructure.Clients.Voice.OpenAiSpeechToText(
                    http, settings.Stt.OpenAi?.Model ?? "whisper-1", key,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenAiSpeechToText>>());
            }
            else if (settings.Stt.Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                          ?? throw new InvalidOperationException("OPENROUTER_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openrouter");
                inner = new Infrastructure.Clients.Voice.OpenRouterSpeechToText(
                    http,
                    settings.Stt.OpenRouter?.Model ?? "openai/whisper-1",
                    key,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenRouterSpeechToText>>());
            }
            else
            {
                inner = new McpChannelVoice.Services.Stt.WyomingSpeechToText(
                    settings.Stt.Wyoming ?? throw new InvalidOperationException("Stt.Wyoming missing"),
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.WyomingSpeechToText>>());
            }

            return McpChannelVoice.Services.Stt.SegmentedSpeechToText.Wrap(
                inner, settings.Stt.Streaming, sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddHostedService<WyomingSatelliteHost>();
        services.AddSingleton(settings.WyomingClient);

        services.AddSingleton<ReplyTextAccumulator>();

        services.AddSingleton<ITextToSpeech>(sp =>
        {
            ITextToSpeech impl;
            if (settings.Tts.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? throw new InvalidOperationException("OPENAI_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
                impl = new Infrastructure.Clients.Voice.OpenAiTextToSpeech(
                    http,
                    settings.Tts.OpenAi?.Model ?? "tts-1",
                    settings.Tts.OpenAi?.Voice ?? "alloy",
                    key,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenAiTextToSpeech>>());
            }
            else if (settings.Tts.Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                          ?? throw new InvalidOperationException("OPENROUTER_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openrouter");
                impl = new Infrastructure.Clients.Voice.OpenAiTextToSpeech(
                    http,
                    settings.Tts.OpenRouter?.Model ?? "openai/gpt-4o-mini-tts",
                    settings.Tts.OpenRouter?.Voice ?? "alloy",
                    key,
                    sp.GetRequiredService<IMetricsPublisher>(),
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenAiTextToSpeech>>(),
                    endpointPath: "/api/v1/audio/speech");
            }
            else
            {
                impl = new McpChannelVoice.Services.Tts.WyomingTextToSpeech(
                    settings.Tts.Wyoming ?? throw new InvalidOperationException("Tts.Wyoming missing"),
                    sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.WyomingTextToSpeech>>());
            }

            return impl;
        });

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